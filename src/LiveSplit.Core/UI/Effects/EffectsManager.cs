using LiveSplit.Model;
using LiveSplit.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace LiveSplit.UI.Effects;

/// <summary>
/// Spawns effects from timer events, advances them, and draws them on top of the layout.
/// When no effect is playing it does no work, so the timer window redraws exactly as before.
/// </summary>
public sealed class EffectsManager : IDisposable
{
    // Longest step an effect advances in one tick, so a stalled window doesn't skip an effect entirely.
    private const double MaxStepSeconds = 0.1;

    private readonly object _sync = new();
    private readonly List<Effect> _effects = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly LiveSplitState _state;
    private readonly Func<LayoutSettings> _settings;
    private double _lastTickSeconds;
    private volatile bool _hasActiveEffects;

    public EffectsManager(LiveSplitState state)
        : this(() => state.LayoutSettings)
    {
        _state = state;
        _state.OnSplit += State_OnSplit;
        _state.OnUndoSplit += State_OnUndoSplit;
        _state.OnReset += State_OnReset;
        _state.OnStart += State_OnStart;
    }

    /// <summary>
    /// Creates a manager that isn't driven by a timer, for previews that play effects on demand.
    /// </summary>
    public EffectsManager(Func<LayoutSettings> settings)
    {
        _settings = settings;
    }

    public EffectAnchors Anchors { get; } = new();

    /// <summary>
    /// True while at least one effect is playing. Safe to read from any thread.
    /// </summary>
    public bool HasActiveEffects => _hasActiveEffects;

    public void Spawn(Effect effect)
    {
        lock (_sync)
        {
            if (_effects.Count == 0)
            {
                // Don't count the idle time before this effect as its first step.
                _lastTickSeconds = _clock.Elapsed.TotalSeconds;
            }

            effect.SpawnedAtSeconds = _clock.Elapsed.TotalSeconds;
            _effects.Add(effect);
            _hasActiveEffects = true;
        }
    }

    /// <summary>
    /// Advances all effects. Call once per refresh tick on the UI thread.
    /// </summary>
    /// <returns>The window area that has to be repainted, or an empty rectangle.</returns>
    public Rectangle Tick()
    {
        lock (_sync)
        {
            if (_effects.Count == 0)
            {
                _hasActiveEffects = false;
                return Rectangle.Empty;
            }

            double now = _clock.Elapsed.TotalSeconds;
            double delta = Math.Min(now - _lastTickSeconds, MaxStepSeconds);
            _lastTickSeconds = now;

            bool enabled = _settings()?.EffectsEnabled ?? false;
            RectangleF dirty = RectangleF.Empty;
            for (int i = _effects.Count - 1; i >= 0; i--)
            {
                Effect effect = _effects[i];
                dirty = Union(dirty, effect.LastDrawnBounds);
                if (!effect.IsStopped && effect is PbBurstEffect burst && !burst.TryStart(Anchors))
                {
                    // Waits for the window to be painted once, unless it has been waiting too long.
                    if (_clock.Elapsed.TotalSeconds - effect.SpawnedAtSeconds < 1.0)
                    {
                        continue;
                    }
                }

                if (!enabled || effect.IsStopped || !effect.Advance(delta))
                {
                    effect.OnFinished();
                    _effects.RemoveAt(i);
                    continue;
                }

                dirty = Union(dirty, effect.GetBounds(Anchors));
            }

            _hasActiveEffects = _effects.Count > 0;
            return ToPixelRectangle(dirty);
        }
    }

    /// <summary>
    /// Draws all playing effects. Call at the end of painting the timer window.
    /// </summary>
    public void Draw(Graphics g)
    {
        lock (_sync)
        {
            if (_effects.Count == 0)
            {
                return;
            }

            Matrix oldTransform = g.Transform;
            Region oldClip = g.Clip;
            try
            {
                g.ResetTransform();
                foreach (Effect effect in _effects)
                {
                    RectangleF bounds = effect.GetBounds(Anchors);
                    if (bounds.IsEmpty || effect.IsStopped)
                    {
                        continue;
                    }

                    effect.Draw(g, Anchors);
                    effect.LastDrawnBounds = bounds;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                foreach (Effect effect in _effects)
                {
                    effect.OnFinished();
                }

                _effects.Clear();
            }
            finally
            {
                g.Transform = oldTransform;
                g.Clip = oldClip;
                oldTransform.Dispose();
                oldClip.Dispose();
            }
        }
    }

    private void State_OnSplit(object sender, EventArgs e)
    {
        LayoutSettings settings = _settings();
        if (settings == null || !settings.EffectsEnabled)
        {
            return;
        }

        int splitIndex = _state.CurrentSplitIndex - 1;
        if (splitIndex < 0 || splitIndex >= _state.Run.Count)
        {
            return;
        }

        if (LiveSplitStateHelper.CheckBestSegment(_state, splitIndex, _state.CurrentTimingMethod))
        {
            SpawnBestSegmentEffects(settings, splitIndex, LiveSplitStateHelper.GetBestSegmentColor(_state), GetTimeSave(splitIndex), GetGoldStreak(splitIndex));
        }

        if (settings.LosingTimePulse && GetTimeLost(settings, splitIndex) is TimeSpan lost)
        {
            SpawnLosingTimeEffects(settings, splitIndex, lost);
        }

        if (_state.CurrentPhase == TimerPhase.Ended && IsNewPersonalBest())
        {
            SpawnPersonalBestEffects(settings, splitIndex);
        }
    }

    /// <summary>
    /// Plays the best segment effects on a split row as if it had just been golded,
    /// saving 3.4 seconds as the third best segment in a row, so the settings can be previewed.
    /// </summary>
    /// <param name="preferredSplitIndex">The split to play them on, or -1 for the last split done.</param>
    /// <returns>False if no split row is on screen to play them on.</returns>
    public bool PreviewBestSegment(int preferredSplitIndex = -1)
    {
        LayoutSettings settings = _settings();
        if (settings == null || !settings.EffectsEnabled)
        {
            return false;
        }

        if (!Anchors.TryGetSplitRowNear(GetPreviewSplitIndex(preferredSplitIndex), out int splitIndex))
        {
            return false;
        }

        StopAll();
        SpawnBestSegmentEffects(settings, splitIndex, settings.BestSegmentColor, TimeSpan.FromSeconds(3.4), 3);
        return true;
    }

    /// <summary>
    /// Plays the losing time effects on a split row as if it had just lost twice the threshold, so the settings can be previewed.
    /// </summary>
    /// <param name="preferredSplitIndex">The split to play them on, or -1 for the last split done.</param>
    /// <returns>False if no split row is on screen to play them on.</returns>
    public bool PreviewLosingTime(int preferredSplitIndex = -1)
    {
        LayoutSettings settings = _settings();
        if (settings == null || !settings.EffectsEnabled)
        {
            return false;
        }

        if (!Anchors.TryGetSplitRowNear(GetPreviewSplitIndex(preferredSplitIndex), out int splitIndex))
        {
            return false;
        }

        StopAll();
        SpawnLosingTimeEffects(settings, splitIndex, TimeSpan.FromSeconds(Math.Max(0.1f, settings.LosingTimeThreshold) * 2f));
        return true;
    }

    private int GetPreviewSplitIndex(int preferredSplitIndex)
    {
        if (preferredSplitIndex >= 0)
        {
            return preferredSplitIndex;
        }

        return _state != null ? Math.Max(_state.CurrentSplitIndex - 1, 0) : 0;
    }

    /// <summary>
    /// Plays the new personal best effects, so the settings can be previewed.
    /// </summary>
    public void PreviewPersonalBest()
    {
        LayoutSettings settings = _settings();
        if (settings == null || !settings.EffectsEnabled)
        {
            return;
        }

        StopAll();
        SpawnPersonalBestEffects(settings, -1);
    }

    private void SpawnBestSegmentEffects(LayoutSettings settings, int splitIndex, Color goldColor, TimeSpan? timeSave, int streak)
    {
        float intensity = Math.Max(0f, Math.Min(1f, settings.EffectsIntensity));
        if (settings.GoldSplitFlash)
        {
            Spawn(new GoldSplitFlashEffect(splitIndex, goldColor, settings.EffectsAccentColor, settings.EffectsStyle, settings.EffectsIntensity, settings.GoldFlashDuration));
        }

        if (settings.GoldTimeSave && timeSave is TimeSpan saved)
        {
            Spawn(new TimeSaveNumberEffect(splitIndex, saved, goldColor, settings.TimesFont.FontFamily, settings));
        }

        if (settings.GoldStreak && streak >= 2)
        {
            Spawn(new GoldStreakEffect(splitIndex, streak, goldColor, settings.TimesFont.FontFamily, settings));
        }

        if (settings.ScreenShake)
        {
            // Longer streaks shake harder.
            float streakBoost = 1f + (0.15f * Math.Min(Math.Max(streak - 1, 0), 4));
            Spawn(new ScreenShakeEffect(splitIndex, settings.ShakeStrength * 0.6f * streakBoost * intensity, 0.35));
        }
    }

    private void SpawnLosingTimeEffects(LayoutSettings settings, int splitIndex, TimeSpan lost)
    {
        // Losing three times the threshold or more pulses at full strength.
        float threshold = Math.Max(0.1f, settings.LosingTimeThreshold);
        float severity = (float)((lost.TotalSeconds - threshold) / (threshold * 2f));
        Spawn(new LosingTimePulseEffect(splitIndex, settings.BehindLosingTimeColor, settings.EffectsStyle, settings.EffectsIntensity, settings.LosingTimePulseDuration, severity));
    }

    private void SpawnPersonalBestEffects(LayoutSettings settings, int splitIndex)
    {
        if (!settings.PbBurst)
        {
            return;
        }

        Spawn(new PbBurstEffect(splitIndex, settings.BestSegmentColor, settings.PersonalBestColor, settings));
        if (settings.ScreenShake)
        {
            float intensity = Math.Max(0f, Math.Min(1f, settings.EffectsIntensity));
            Spawn(new ScreenShakeEffect(splitIndex, settings.ShakeStrength * 1.3f * intensity, 0.7));
        }
    }

    /// <summary>
    /// How far the layout should be pushed by screen shakes right now, in window pixels.
    /// </summary>
    public PointF GetShakeOffset()
    {
        if (!_hasActiveEffects)
        {
            return PointF.Empty;
        }

        lock (_sync)
        {
            float x = 0f, y = 0f;
            foreach (Effect effect in _effects)
            {
                if (effect is ScreenShakeEffect shake && !effect.IsStopped)
                {
                    PointF offset = shake.Offset;
                    x += offset.X;
                    y += offset.Y;
                }
            }

            return new PointF(x, y);
        }
    }

    // How many best segments in a row end at splitIndex, counting it.
    private int GetGoldStreak(int splitIndex)
    {
        int streak = 0;
        for (int i = splitIndex; i >= 0 && LiveSplitStateHelper.CheckBestSegment(_state, i, _state.CurrentTimingMethod); i--)
        {
            streak++;
        }

        return streak;
    }

    // How much faster the segment ending at splitIndex was than the best segment, or null if there was no best segment yet.
    private TimeSpan? GetTimeSave(int splitIndex)
    {
        TimingMethod method = _state.CurrentTimingMethod;
        TimeSpan? segment = LiveSplitStateHelper.GetPreviousSegmentTime(_state, splitIndex, method);
        TimeSpan? bestSegment = _state.Run[splitIndex].BestSegmentTime[method];
        if (segment == null || bestSegment == null || segment >= bestSegment)
        {
            return null;
        }

        return bestSegment - segment;
    }

    // How much time the segment ending at splitIndex lost against the current comparison, or null if it lost less than the threshold.
    private TimeSpan? GetTimeLost(LayoutSettings settings, int splitIndex)
    {
        TimeSpan? lost = LiveSplitStateHelper.GetPreviousSegmentDelta(_state, splitIndex, _state.CurrentComparison, _state.CurrentTimingMethod);
        if (lost == null || lost.Value.TotalSeconds < Math.Max(0.1f, settings.LosingTimeThreshold))
        {
            return null;
        }

        return lost;
    }

    private bool IsNewPersonalBest()
    {
        TimingMethod method = _state.CurrentTimingMethod;
        ISegment last = _state.Run[_state.Run.Count - 1];
        TimeSpan? time = last.SplitTime[method];
        TimeSpan? personalBest = last.PersonalBestSplitTime[method];
        return time != null && (personalBest == null || time < personalBest);
    }

    private void State_OnUndoSplit(object sender, EventArgs e)
    {
        lock (_sync)
        {
            // Effects on the undone split stop; their last frame is erased on the next tick.
            foreach (Effect effect in _effects)
            {
                if (effect.SplitIndex >= _state.CurrentSplitIndex)
                {
                    effect.IsStopped = true;
                }
            }
        }
    }

    private void State_OnReset(object sender, TimerPhase e)
    {
        StopAll();
    }

    private void State_OnStart(object sender, EventArgs e)
    {
        StopAll();
    }

    /// <summary>
    /// Ends all playing effects; their last frame is erased on the next tick.
    /// </summary>
    public void StopAll()
    {
        lock (_sync)
        {
            foreach (Effect effect in _effects)
            {
                effect.IsStopped = true;
            }
        }
    }

    private static RectangleF Union(RectangleF a, RectangleF b)
    {
        if (a.IsEmpty)
        {
            return b;
        }

        if (b.IsEmpty)
        {
            return a;
        }

        return RectangleF.Union(a, b);
    }

    private static Rectangle ToPixelRectangle(RectangleF rect)
    {
        if (rect.IsEmpty)
        {
            return Rectangle.Empty;
        }

        // Pad by a couple of pixels for anti-aliased edges.
        return Rectangle.FromLTRB(
            (int)Math.Floor(rect.Left) - 2,
            (int)Math.Floor(rect.Top) - 2,
            (int)Math.Ceiling(rect.Right) + 2,
            (int)Math.Ceiling(rect.Bottom) + 2);
    }

    public void Dispose()
    {
        if (_state == null)
        {
            return;
        }

        _state.OnSplit -= State_OnSplit;
        _state.OnUndoSplit -= State_OnUndoSplit;
        _state.OnReset -= State_OnReset;
        _state.OnStart -= State_OnStart;
    }
}
