using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

using LiveSplit.Model;

namespace PogSplit.TwitchBridge;

/// <summary>
/// Builds the run history the overlay's history tabs show: recent attempts, finished runs with their splits, and
/// golds. It comes from the splits file's attempt and segment history, which LiveSplit updates on reset.
/// Must be called on the thread that owns the state (the UI thread).
/// </summary>
public static class HistoryBuilder
{
    private const int MaxAttempts = 50;
    private const int MaxFinished = 10;
    private const int MaxGolds = 50;

    public static HistoryMessage Build(IRun run, TimingMethod method)
    {
        var segments = run.Select(s => s.Name ?? "").ToList();
        var attempts = new List<AttemptInfo>();
        var finished = new List<FinishedRunInfo>();

        foreach (Attempt attempt in run.AttemptHistory.OrderByDescending(a => a.Index))
        {
            if (attempts.Count >= MaxAttempts && finished.Count >= MaxFinished)
            {
                break;
            }

            long? final = MessageBuilder.Ms(attempt.Time[method]);
            if (attempts.Count >= MaxAttempts && final == null)
            {
                // Still looking for finished runs: skip resets without rebuilding their splits.
                continue;
            }

            List<long?> splits = Splits(run, attempt.Index, method, out int reached);
            if (attempts.Count < MaxAttempts)
            {
                attempts.Add(new AttemptInfo
                {
                    Id = attempt.Index,
                    Started = Epoch(attempt.Started),
                    Ended = Epoch(attempt.Ended),
                    Time = final,
                    Reached = final != null ? run.Count : reached,
                    Last = splits.Take(reached).LastOrDefault(t => t != null),
                });
            }

            if (final != null && finished.Count < MaxFinished)
            {
                splits[splits.Count - 1] = final;
                finished.Add(new FinishedRunInfo
                {
                    Id = attempt.Index,
                    Started = Epoch(attempt.Started),
                    Ended = Epoch(attempt.Ended),
                    Time = final,
                    Splits = splits,
                });
            }
        }

        return new HistoryMessage
        {
            TimingMethod = method.ToString(),
            Segments = segments,
            Attempts = attempts,
            Finished = finished,
            Golds = Golds(run, method),
        };
    }

    /// <summary>
    /// Split times of one attempt, rebuilt from its segment times. A skipped split is null, and the segment after it
    /// holds the combined time, so the running total stays right.
    /// </summary>
    private static List<long?> Splits(IRun run, int attemptId, TimingMethod method, out int reached)
    {
        var splits = new List<long?>(run.Count);
        TimeSpan total = TimeSpan.Zero;
        reached = 0;
        foreach (ISegment segment in run)
        {
            if (!segment.SegmentHistory.TryGetValue(attemptId, out Time time))
            {
                splits.Add(null);
                continue;
            }

            reached = splits.Count + 1;
            TimeSpan? t = time[method];
            if (t != null)
            {
                total += t.Value;
                splits.Add(MessageBuilder.Ms(total));
            }
            else
            {
                splits.Add(null);
            }
        }

        return splits;
    }

    /// <summary>
    /// Every time a segment beat its best so far, newest first. Times right after a skipped split cover two segments
    /// and are left out. History imported without an attempt (index 0 or below) only sets the starting best.
    /// </summary>
    private static List<GoldInfo> Golds(IRun run, TimingMethod method)
    {
        var dates = run.AttemptHistory.ToDictionary(a => a.Index, a => Epoch(a.Ended) ?? Epoch(a.Started));
        var golds = new List<GoldInfo>();

        for (int i = 0; i < run.Count; i++)
        {
            TimeSpan? best = null;
            foreach (KeyValuePair<int, Time> entry in run[i].SegmentHistory.OrderBy(e => e.Key))
            {
                TimeSpan? t = entry.Value[method];
                if (t == null || t.Value <= TimeSpan.Zero)
                {
                    continue;
                }

                if (i > 0 && run[i - 1].SegmentHistory.TryGetValue(entry.Key, out Time prev) && prev[method] == null)
                {
                    continue;
                }

                if (best == null || t.Value < best.Value)
                {
                    if (entry.Key > 0)
                    {
                        golds.Add(new GoldInfo
                        {
                            Segment = i,
                            Id = entry.Key,
                            Time = MessageBuilder.Ms(t),
                            Previous = MessageBuilder.Ms(best),
                            Date = dates.TryGetValue(entry.Key, out long? date) ? date : null,
                        });
                    }

                    best = t;
                }
            }
        }

        return golds
            .OrderByDescending(g => g.Id)
            .ThenByDescending(g => g.Segment)
            .Take(MaxGolds)
            .ToList();
    }

    private static long? Epoch(AtomicDateTime? time)
    {
        if (time == null)
        {
            return null;
        }

        // LiveSplit keeps attempt times in UTC.
        DateTime t = time.Value.Time;
        t = t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);
        return new DateTimeOffset(t).ToUnixTimeMilliseconds();
    }
}

public class HistoryMessage
{
    [JsonPropertyName("v")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("timingMethod")]
    public string TimingMethod { get; set; }

    [JsonPropertyName("segments")]
    public List<string> Segments { get; set; }

    [JsonPropertyName("attempts")]
    public List<AttemptInfo> Attempts { get; set; }

    [JsonPropertyName("finished")]
    public List<FinishedRunInfo> Finished { get; set; }

    [JsonPropertyName("golds")]
    public List<GoldInfo> Golds { get; set; }
}

public class AttemptInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("started")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Started { get; set; }

    [JsonPropertyName("ended")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Ended { get; set; }

    /// <summary>Final time, or null when the attempt was reset.</summary>
    [JsonPropertyName("time")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Time { get; set; }

    /// <summary>Splits completed before the attempt ended.</summary>
    [JsonPropertyName("reached")]
    public int Reached { get; set; }

    /// <summary>Time of the last completed split.</summary>
    [JsonPropertyName("last")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Last { get; set; }
}

public class FinishedRunInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("started")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Started { get; set; }

    [JsonPropertyName("ended")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Ended { get; set; }

    [JsonPropertyName("time")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Time { get; set; }

    [JsonPropertyName("splits")]
    public List<long?> Splits { get; set; }
}

public class GoldInfo
{
    [JsonPropertyName("seg")]
    public int Segment { get; set; }

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("time")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Time { get; set; }

    /// <summary>The best it beat, or null for the first time recorded.</summary>
    [JsonPropertyName("prev")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Previous { get; set; }

    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Date { get; set; }
}
