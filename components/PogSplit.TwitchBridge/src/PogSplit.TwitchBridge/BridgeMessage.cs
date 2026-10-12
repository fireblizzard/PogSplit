using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PogSplit.TwitchBridge;

// Mirrors twitch-extension/schema/run-state.schema.json (v1). All times are integer milliseconds.
public class BridgeMessage
{
    public const string Snapshot = "snapshot";
    public const string Update = "update";
    public const string Offline = "offline";

    [JsonPropertyName("v")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("run")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Run { get; set; }

    // PogSplit's own send order. The EBS uses it to drop stale requests and stamps its own seq on publish.
    [JsonPropertyName("seq")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Seq { get; set; }

    [JsonPropertyName("game")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Game { get; set; }

    [JsonPropertyName("category")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Category { get; set; }

    [JsonPropertyName("attempts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Attempts { get; set; }

    [JsonPropertyName("timingMethod")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string TimingMethod { get; set; }

    [JsonPropertyName("comparisons")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string> Comparisons { get; set; }

    [JsonPropertyName("segments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SegmentInfo> Segments { get; set; }

    [JsonPropertyName("splits")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<long?> Splits { get; set; }

    [JsonPropertyName("best")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<long?> Best { get; set; }

    [JsonPropertyName("timer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimerInfo Timer { get; set; }

    [JsonPropertyName("fx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<EffectInfo> Fx { get; set; }

    /// <summary>
    /// Folds a newer message into an older unsent one, so a pending snapshot is never lost to a later update
    /// and no effect hint is dropped.
    /// </summary>
    public static BridgeMessage Merge(BridgeMessage older, BridgeMessage newer)
    {
        if (older == null)
        {
            return newer;
        }

        if (newer == null)
        {
            return older;
        }

        if (newer.Type == Offline || older.Type == Offline)
        {
            return newer;
        }

        List<EffectInfo> fx = ConcatFx(older.Fx, newer.Fx);

        if (older.Type == Snapshot && newer.Type == Update && older.Run == newer.Run)
        {
            older.Seq = newer.Seq;
            older.Splits = newer.Splits ?? older.Splits;
            older.Timer = newer.Timer ?? older.Timer;
            if (newer.Best != null && older.Segments != null)
            {
                for (int i = 0; i < older.Segments.Count && i < newer.Best.Count; i++)
                {
                    older.Segments[i].Best = newer.Best[i];
                }
            }

            older.Fx = fx;
            return older;
        }

        if (newer.Type == Update && older.Type == Update && newer.Best == null)
        {
            newer.Best = older.Best;
        }

        newer.Fx = fx;
        return newer;
    }

    private static List<EffectInfo> ConcatFx(List<EffectInfo> a, List<EffectInfo> b)
    {
        if (a == null || a.Count == 0)
        {
            return b;
        }

        if (b == null || b.Count == 0)
        {
            return a;
        }

        var all = new List<EffectInfo>(a);
        all.AddRange(b);
        return all;
    }
}

public class SegmentInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("best")]
    public long? Best { get; set; }

    [JsonPropertyName("cmp")]
    public List<long?> Cmp { get; set; }
}

public class TimerInfo
{
    [JsonPropertyName("phase")]
    public string Phase { get; set; }

    [JsonPropertyName("time")]
    public long Time { get; set; }

    // Unix epoch milliseconds at which Time was valid, on PogSplit's clock. The EBS rewrites it into its own clock.
    [JsonPropertyName("at")]
    public long At { get; set; }

    [JsonPropertyName("splitIndex")]
    public int SplitIndex { get; set; }
}

public class EffectInfo
{
    public const string Gold = "gold";
    public const string PersonalBest = "pb";
    public const string Reset = "reset";
    public const string Undo = "undo";

    [JsonPropertyName("k")]
    public string Kind { get; set; }

    [JsonPropertyName("i")]
    public int Index { get; set; }
}
