using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;

using LiveSplit.Model;
using LiveSplit.UI;
using LiveSplit.UI.Components;

namespace PogSplit.TwitchBridge;

/// <summary>
/// An invisible layout component that publishes the run to the PogSplit Twitch extension.
/// Timer events only mark what changed; messages are built in <see cref="Update"/> on the UI thread and handed to
/// <see cref="BridgeSender"/>, which does the network work in the background.
/// </summary>
public class TwitchBridgeComponent : LogicComponent
{
    public const string Name = "PogSplit Twitch Bridge";

    // Placeholder until the EBS is deployed; streamers can override it in the component settings.
    public const string DefaultEbsUrl = "https://pogsplit-ebs.pogsplit-ebs.workers.dev";

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ActiveHeartbeat = TimeSpan.FromSeconds(15);
    // Must stay under the backend's 45 s offline timeout, or idle channels flicker offline between heartbeats.
    private static readonly TimeSpan IdleHeartbeat = TimeSpan.FromSeconds(30);
    private const long DriftToleranceMs = 500;
    private static readonly TimeSpan HistoryMinInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HistoryRetry = TimeSpan.FromSeconds(30);

    private readonly LiveSplitState state;
    private readonly TwitchBridgeSettings settings;
    private readonly List<EffectInfo> effects = [];

    private EbsClient client;
    private BridgeSender sender;

    private bool startNeeded;
    private bool snapshotNeeded = true;
    private DateTime? dirtySince;
    private DateTime lastSent = DateTime.MinValue;
    private string runHash;
    private TimerInfo lastTimer;
    private long lastRawTime; // the timer value LiveSplit had when lastTimer was sent, before extrapolation

    private IRun lastRun;
    private string lastComparison;
    private TimingMethod lastMethod;
    private string lastPhase;

    private volatile bool historyNeeded;
    private volatile bool historySending;
    private DateTime historyNotBefore = DateTime.MinValue;

    public override string ComponentName => Name;

    public string EbsUrl { get; private set; } = DefaultEbsUrl;

    /// <summary>
    /// Lets the overlay timer run while game time is paused, for auto splitters such as BunnySplit (BXT) that keep
    /// game time paused and only set it at splits. During real loads the overlay then runs on and is corrected after.
    /// </summary>
    public bool KeepTimerRunning { get; internal set; } = true;

    /// <summary>
    /// Lets viewers browse past attempts, finished runs and golds in the overlay's history tabs.
    /// </summary>
    public bool ShareHistory { get; internal set; } = true;

    public Pairing Pairing { get; private set; }

    public BridgeStatus Status => sender?.Status ?? BridgeStatus.NotConnected;

    public event Action<BridgeStatus> StatusChanged;

    public TwitchBridgeComponent(LiveSplitState state)
    {
        this.state = state;
        settings = new TwitchBridgeSettings(this);
        ContextMenuControls = new Dictionary<string, Action>();

        state.OnStart += State_OnStart;
        state.OnSplit += State_OnSplit;
        state.OnSkipSplit += State_OnChanged;
        state.OnUndoSplit += State_OnUndoSplit;
        state.OnReset += State_OnReset;
        state.OnPause += State_OnChanged;
        state.OnResume += State_OnChanged;
        state.OnUndoAllPauses += State_OnChanged;
        state.OnSwitchComparisonPrevious += State_OnRunChanged;
        state.OnSwitchComparisonNext += State_OnRunChanged;
        state.RunManuallyModified += State_OnRunChanged;
        state.ComparisonRenamed += State_OnRunChanged;

        Pairing = TokenStore.Load();
        Restart();
    }

    public override void Update(IInvalidator invalidator, LiveSplitState state, float width, float height, LayoutMode mode)
    {
        if (startNeeded)
        {
            StartSender();
        }

        if (sender == null || sender.Status == BridgeStatus.Unauthorized)
        {
            return;
        }

        PollUnevented();
        SendHistoryIfNeeded();

        DateTime now = DateTime.UtcNow;
        bool due = (dirtySince.HasValue && now - dirtySince.Value >= Debounce)
            || now - lastSent >= HeartbeatInterval()
            || HasDrifted();

        if (!due)
        {
            return;
        }

        BridgeMessage message;
        if (snapshotNeeded || runHash == null)
        {
            message = MessageBuilder.BuildSnapshot(state, KeepTimerRunning);
            runHash = message.Run;
            snapshotNeeded = false;
        }
        else
        {
            message = MessageBuilder.BuildUpdate(state, runHash, KeepTimerRunning);
        }

        long rawTime = message.Timer.Time;
        if (GameTimeFrozen() && rawTime == lastRawTime && lastTimer?.Phase == nameof(TimerPhase.Running) && message.Timer.Phase == lastTimer.Phase)
        {
            // Nothing new from the game: keep counting from what the overlay already shows instead of sending it back.
            message.Timer.Time = lastTimer.Time + (message.Timer.At - lastTimer.At);
        }

        lastRawTime = rawTime;

        if (effects.Count > 0)
        {
            message.Fx = [.. effects];
            effects.Clear();
        }

        lastTimer = message.Timer;
        lastSent = now;
        dirtySince = null;
        sender.Offer(message);
    }

    /// <summary>
    /// Some changes raise no event: picking a comparison from the menu, switching timing method, opening another
    /// splits file, and game time pausing during loads.
    /// </summary>
    private void PollUnevented()
    {
        if (!ReferenceEquals(state.Run, lastRun) || state.CurrentComparison != lastComparison || state.CurrentTimingMethod != lastMethod)
        {
            lastRun = state.Run;
            lastComparison = state.CurrentComparison;
            lastMethod = state.CurrentTimingMethod;
            snapshotNeeded = true;
            historyNeeded = true;
            MarkDirty();
        }

        string phase = MessageBuilder.EffectivePhase(state, KeepTimerRunning);
        if (phase != lastPhase)
        {
            lastPhase = phase;
            MarkDirty();
        }
    }

    private TimeSpan HeartbeatInterval()
    {
        return state.CurrentPhase is TimerPhase.Running or TimerPhase.Paused ? ActiveHeartbeat : IdleHeartbeat;
    }

    /// <summary>
    /// True when the overlay's extrapolated time is off, for example after an auto splitter syncs game time.
    /// </summary>
    private bool HasDrifted()
    {
        if (lastTimer == null || lastTimer.Phase != nameof(TimerPhase.Running) || lastPhase != nameof(TimerPhase.Running))
        {
            return false;
        }

        TimerInfo now = MessageBuilder.Timer(state, KeepTimerRunning);
        if (GameTimeFrozen() && now.Time == lastRawTime)
        {
            // Paused game time only means something once it is set to a new value.
            return false;
        }

        long predicted = lastTimer.Time + (now.At - lastTimer.At);
        return Math.Abs(now.Time - predicted) > DriftToleranceMs;
    }

    /// <summary>
    /// Uploads the run history (or removes it when sharing is off) in the background, one request at a time.
    /// </summary>
    private void SendHistoryIfNeeded()
    {
        if (!historyNeeded || historySending || DateTime.UtcNow < historyNotBefore)
        {
            return;
        }

        historyNeeded = false;
        historySending = true;
        historyNotBefore = DateTime.UtcNow + HistoryMinInterval;

        HistoryMessage history = ShareHistory ? HistoryBuilder.Build(state.Run, state.CurrentTimingMethod) : null;
        EbsClient http = client;
        string token = Pairing.Token;
        Task.Run(async () =>
        {
            try
            {
                if (await http.PutHistoryAsync(token, history, CancellationToken.None).ConfigureAwait(false) == SendResult.Retry)
                {
                    historyNotBefore = DateTime.UtcNow + HistoryRetry;
                    historyNeeded = true;
                }
            }
            catch (ObjectDisposedException)
            {
                // The sender was restarted; the new one uploads the history again.
            }
            finally
            {
                historySending = false;
            }
        });
    }

    internal void SetShareHistory(bool value)
    {
        if (value != ShareHistory)
        {
            ShareHistory = value;
            historyNeeded = true;
        }
    }

    private bool GameTimeFrozen()
    {
        return KeepTimerRunning && state.CurrentTimingMethod == TimingMethod.GameTime && state.IsGameTimePaused;
    }

    internal void SetKeepTimerRunning(bool value)
    {
        if (value != KeepTimerRunning)
        {
            KeepTimerRunning = value;
            MarkDirty();
        }
    }

    private void MarkDirty()
    {
        dirtySince ??= DateTime.UtcNow;
    }

    private void State_OnStart(object sender, EventArgs e)
    {
        snapshotNeeded = true;
        MarkDirty();
    }

    private void State_OnSplit(object sender, EventArgs e)
    {
        int completed = state.CurrentSplitIndex - 1;
        TimingMethod method = state.CurrentTimingMethod;
        if (completed >= 0 && completed < state.Run.Count && LiveSplitStateHelper.CheckBestSegment(state, completed, method))
        {
            effects.Add(new EffectInfo { Kind = EffectInfo.Gold, Index = completed });
        }

        if (state.CurrentPhase == TimerPhase.Ended)
        {
            ISegment last = state.Run[state.Run.Count - 1];
            TimeSpan? final = last.SplitTime[method];
            TimeSpan? pb = last.PersonalBestSplitTime[method];
            if (final != null && (pb == null || final < pb))
            {
                effects.Add(new EffectInfo { Kind = EffectInfo.PersonalBest, Index = completed });
            }
        }

        MarkDirty();
    }

    private void State_OnUndoSplit(object sender, EventArgs e)
    {
        effects.Add(new EffectInfo { Kind = EffectInfo.Undo, Index = state.CurrentSplitIndex });
        MarkDirty();
    }

    private void State_OnReset(object sender, TimerPhase oldPhase)
    {
        // LiveSplit has just saved the attempt into the history.
        historyNeeded = true;
        effects.Add(new EffectInfo { Kind = EffectInfo.Reset, Index = -1 });

        // Reset saves golds, PB and history, so the run part changes.
        snapshotNeeded = true;
        MarkDirty();
    }

    private void State_OnChanged(object sender, EventArgs e)
    {
        MarkDirty();
    }

    private void State_OnRunChanged(object sender, EventArgs e)
    {
        snapshotNeeded = true;
        MarkDirty();
    }

    internal void SetEbsUrl(string url)
    {
        url = string.IsNullOrWhiteSpace(url) ? DefaultEbsUrl : url.Trim();
        if (url != EbsUrl)
        {
            EbsUrl = url;
            Restart();
        }
    }

    internal EbsClient CreateClient(TimeSpan timeout)
    {
        return new EbsClient(EbsUrl, timeout);
    }

    internal void SetPairing(Pairing pairing)
    {
        if (pairing == null)
        {
            TokenStore.Clear();
        }
        else
        {
            TokenStore.Save(pairing);
        }

        Pairing = pairing;
        Restart();
    }

    /// <summary>
    /// Stops sending now and starts again on the next <see cref="Update"/>, after layout settings have been applied.
    /// </summary>
    private void Restart()
    {
        StopSender();
        startNeeded = Pairing != null;
        StatusChanged?.Invoke(BridgeStatus.NotConnected);
    }

    private void StartSender()
    {
        startNeeded = false;
        client = new EbsClient(EbsUrl, TimeSpan.FromSeconds(10));
        sender = new BridgeSender(client, Pairing.Token);
        sender.StatusChanged += s => StatusChanged?.Invoke(s);
        snapshotNeeded = true;
        runHash = null;
        lastTimer = null;
        dirtySince = DateTime.MinValue;
        historyNeeded = true;
        historyNotBefore = DateTime.MinValue;
        StatusChanged?.Invoke(sender.Status);
    }

    private void StopSender()
    {
        sender?.Dispose();
        client?.Dispose();
        sender = null;
        client = null;
    }

    public override Control GetSettingsControl(LayoutMode mode)
    {
        return settings;
    }

    public override XmlNode GetSettings(XmlDocument document)
    {
        XmlElement parent = document.CreateElement("Settings");
        parent.AppendChild(SettingsHelper.ToElement(document, "Version", "1.0"));
        parent.AppendChild(SettingsHelper.ToElement(document, "EbsUrl", EbsUrl == DefaultEbsUrl ? "" : EbsUrl));
        parent.AppendChild(SettingsHelper.ToElement(document, "KeepTimerRunning", KeepTimerRunning));
        parent.AppendChild(SettingsHelper.ToElement(document, "ShareHistory", ShareHistory));
        return parent;
    }

    public override void SetSettings(XmlNode settings)
    {
        var element = (XmlElement)settings;
        SetEbsUrl(element["EbsUrl"]?.InnerText);
        SetKeepTimerRunning(SettingsHelper.ParseBool(element["KeepTimerRunning"], true));
        SetShareHistory(SettingsHelper.ParseBool(element["ShareHistory"], true));
        this.settings.Refresh();
    }

    public int GetSettingsHashCode()
    {
        return EbsUrl.GetHashCode() ^ KeepTimerRunning.GetHashCode() ^ (ShareHistory.GetHashCode() << 1);
    }

    public override void Dispose()
    {
        state.OnStart -= State_OnStart;
        state.OnSplit -= State_OnSplit;
        state.OnSkipSplit -= State_OnChanged;
        state.OnUndoSplit -= State_OnUndoSplit;
        state.OnReset -= State_OnReset;
        state.OnPause -= State_OnChanged;
        state.OnResume -= State_OnChanged;
        state.OnUndoAllPauses -= State_OnChanged;
        state.OnSwitchComparisonPrevious -= State_OnRunChanged;
        state.OnSwitchComparisonNext -= State_OnRunChanged;
        state.RunManuallyModified -= State_OnRunChanged;
        state.ComparisonRenamed -= State_OnRunChanged;

        StopSender();
        settings.Dispose();
    }
}
