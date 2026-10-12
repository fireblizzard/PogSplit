using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace PogSplit.TwitchBridge;

/// <summary>
/// Builds overlay messages from <see cref="LiveSplitState"/>. Must be called on the thread that owns the state (the UI thread).
/// </summary>
public static class MessageBuilder
{
    public static BridgeMessage BuildSnapshot(LiveSplitState state, bool keepRunning = false)
    {
        TimingMethod method = state.CurrentTimingMethod;
        IRun run = state.Run;

        List<string> comparisons = OrderedComparisons(state);
        var segments = new List<SegmentInfo>(run.Count);
        foreach (ISegment segment in run)
        {
            segments.Add(new SegmentInfo
            {
                Name = segment.Name,
                Best = Ms(segment.BestSegmentTime[method]),
                Cmp = comparisons.Select(c => Ms(ComparisonTime(segment, c, method))).ToList(),
            });
        }

        var message = new BridgeMessage
        {
            Type = BridgeMessage.Snapshot,
            Game = run.GameName ?? "",
            Category = run.CategoryName ?? "",
            Attempts = run.AttemptCount,
            TimingMethod = method.ToString(),
            Comparisons = comparisons,
            Segments = segments,
        };

        // The hash covers only the run part, so it changes when splits, comparisons or attempts change, not on every tick.
        message.Run = Hash(message);
        message.Splits = Splits(state);
        message.Timer = Timer(state, keepRunning);
        return message;
    }

    public static BridgeMessage BuildUpdate(LiveSplitState state, string runHash, bool keepRunning = false)
    {
        return new BridgeMessage
        {
            Type = BridgeMessage.Update,
            Run = runHash,
            Splits = Splits(state),
            Timer = Timer(state, keepRunning),
        };
    }

    public static BridgeMessage BuildOffline()
    {
        return new BridgeMessage { Type = BridgeMessage.Offline };
    }

    /// <summary>
    /// The phase the overlay should show. While game time is the active method and it is paused (loads),
    /// the overlay is told "Paused" so it stops extrapolating, unless <paramref name="keepRunning"/> is set:
    /// some auto splitters (BunnySplit) keep game time paused and only set it now and then.
    /// </summary>
    public static string EffectivePhase(LiveSplitState state, bool keepRunning = false)
    {
        TimerPhase phase = state.CurrentPhase;
        if (phase == TimerPhase.Running
            && !keepRunning
            && state.CurrentTimingMethod == TimingMethod.GameTime
            && (state.IsGameTimePaused || state.CurrentTime.GameTime == null))
        {
            return nameof(TimerPhase.Paused);
        }

        return phase.ToString();
    }

    public static TimerInfo Timer(LiveSplitState state, bool keepRunning = false)
    {
        return new TimerInfo
        {
            Phase = EffectivePhase(state, keepRunning),
            Time = Ms(state.CurrentTime[state.CurrentTimingMethod]) ?? 0,
            At = NowMs(),
            SplitIndex = state.CurrentPhase == TimerPhase.NotRunning ? -1 : state.CurrentSplitIndex,
        };
    }

    public static List<long?> Splits(LiveSplitState state)
    {
        var splits = new List<long?>();
        if (state.CurrentPhase == TimerPhase.NotRunning)
        {
            return splits;
        }

        int done = Math.Min(state.CurrentSplitIndex, state.Run.Count);
        for (int i = 0; i < done; i++)
        {
            splits.Add(Ms(state.Run[i].SplitTime[state.CurrentTimingMethod]));
        }

        return splits;
    }

    public static List<string> OrderedComparisons(LiveSplitState state)
    {
        var names = state.Run.Comparisons
            .Where(c => c != NoneComparisonGenerator.ComparisonName)
            .Distinct()
            .ToList();

        // The schema puts the streamer's current comparison first.
        string current = state.CurrentComparison;
        if (names.Remove(current))
        {
            names.Insert(0, current);
        }

        return names;
    }

    public static long NowMs()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static TimeSpan? ComparisonTime(ISegment segment, string comparison, TimingMethod method)
    {
        return segment.Comparisons.TryGetValue(comparison, out Time time) ? time[method] : null;
    }

    internal static long? Ms(TimeSpan? time)
    {
        return time.HasValue ? (long)Math.Round(time.Value.TotalMilliseconds) : null;
    }

    private static string Hash(BridgeMessage runPart)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(runPart);
        using var sha = SHA256.Create();
        byte[] digest = sha.ComputeHash(json);
        var hex = new StringBuilder(16);
        for (int i = 0; i < 8; i++)
        {
            hex.Append(digest[i].ToString("x2"));
        }

        return hex.ToString();
    }
}
