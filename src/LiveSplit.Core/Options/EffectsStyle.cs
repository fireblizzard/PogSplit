namespace LiveSplit.Options;

/// <summary>
/// The look of the split effects, so they can match the mood of a stream.
/// </summary>
public enum EffectsStyle
{
    /// <summary>
    /// The original effects.
    /// </summary>
    Classic,

    /// <summary>
    /// Big, fast and loud: strobing flashes, wobbling numbers, combo counters, screen shake and confetti.
    /// </summary>
    Chaotic,

    /// <summary>
    /// Slow and soft: gentle glows, floating numbers and a calm bloom of light.
    /// </summary>
    Chill,

    /// <summary>
    /// Sci-fi HUD: scanlines, lock-on brackets, decoding digits and grid ripples in a neon accent color.
    /// </summary>
    Tech
}

/// <summary>
/// The effect settings each <see cref="EffectsStyle"/> starts with.
/// </summary>
public static class EffectsPresets
{
    /// <summary>
    /// Sets <paramref name="settings"/> to the style and the tuning values that suit it.
    /// The on/off switches for flash, time saved, losing time pulse and PB burst, the intensity and the accent color are left alone.
    /// </summary>
    public static void Apply(LayoutSettings settings, EffectsStyle style)
    {
        settings.EffectsStyle = style;
        switch (style)
        {
            case EffectsStyle.Chaotic:
                Set(settings, flash: 0.6f, timeSave: 0.9f, fadeStart: 0.6f, rotation: 25f, drift: 1.2f, textSize: 1f, popScale: 1.8f, burst: 2.6f, pulse: 0.7f, streak: true, shake: true);
                break;
            case EffectsStyle.Chill:
                Set(settings, flash: 2f, timeSave: 2.4f, fadeStart: 0.45f, rotation: 0f, drift: -0.6f, textSize: 0.6f, popScale: 1f, burst: 3.2f, pulse: 1.8f, streak: false, shake: false);
                break;
            case EffectsStyle.Tech:
                Set(settings, flash: 1.1f, timeSave: 1.5f, fadeStart: 0.7f, rotation: 0f, drift: 0.3f, textSize: 0.7f, popScale: 1.1f, burst: 2.4f, pulse: 1.1f, streak: true, shake: false);
                break;
            default:
                Set(settings, flash: 0.75f, timeSave: 1f, fadeStart: 0.55f, rotation: 30f, drift: 0.9f, textSize: 0.75f, popScale: 1.25f, burst: 1.8f, pulse: 1f, streak: false, shake: false);
                break;
        }
    }

    private static void Set(LayoutSettings settings, float flash, float timeSave, float fadeStart, float rotation, float drift, float textSize, float popScale, float burst, float pulse, bool streak, bool shake)
    {
        settings.GoldFlashDuration = flash;
        settings.TimeSaveDuration = timeSave;
        settings.TimeSaveFadeStart = fadeStart;
        settings.TimeSaveRotation = rotation;
        settings.TimeSaveDrift = drift;
        settings.TimeSaveTextSize = textSize;
        settings.TimeSavePopScale = popScale;
        settings.PbBurstDuration = burst;
        settings.LosingTimePulseDuration = pulse;
        settings.GoldStreak = streak;
        settings.ScreenShake = shake;
    }
}
