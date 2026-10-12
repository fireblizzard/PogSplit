using System;
using System.Drawing;

namespace LiveSplit.UI.Effects;

/// <summary>
/// A short-lived visual effect drawn by the <see cref="EffectsManager"/> on top of all layout components.
/// Effects are animated by elapsed time, not by frame count, so they play at the same speed at any refresh rate.
/// </summary>
public abstract class Effect
{
    protected Effect(int splitIndex = -1)
    {
        SplitIndex = splitIndex;
    }

    /// <summary>
    /// The split this effect belongs to, or -1 if it isn't tied to a split.
    /// Used to cancel the effect when that split is undone.
    /// </summary>
    public int SplitIndex { get; }

    /// <summary>
    /// Seconds since the effect started.
    /// </summary>
    public double Age { get; private set; }

    /// <summary>
    /// Total length of the effect in seconds.
    /// </summary>
    public abstract double Duration { get; }

    /// <summary>
    /// How far the effect has played, from 0 to 1.
    /// </summary>
    public float Progress => (float)Math.Min(Age / Duration, 1.0);

    /// <summary>
    /// The window area the effect covered the last time it was drawn, in window pixels.
    /// </summary>
    internal RectangleF LastDrawnBounds { get; set; }

    /// <summary>
    /// When the effect was spawned, on the <see cref="EffectsManager"/>'s clock.
    /// </summary>
    internal double SpawnedAtSeconds { get; set; }

    /// <summary>
    /// Set to end the effect early; it is erased and removed on the next tick.
    /// </summary>
    internal bool IsStopped { get; set; }

    /// <summary>
    /// Advances the effect by <paramref name="deltaSeconds"/>.
    /// </summary>
    /// <returns>False once the effect has finished.</returns>
    public virtual bool Advance(double deltaSeconds)
    {
        Age += deltaSeconds;
        return Age < Duration;
    }

    /// <summary>
    /// Called once when the effect is removed, to release any resources it holds.
    /// </summary>
    protected internal virtual void OnFinished()
    {
    }

    /// <summary>
    /// Returns the window area the effect will draw into, in window pixels, or an empty rectangle if it has nothing to draw.
    /// </summary>
    public abstract RectangleF GetBounds(EffectAnchors anchors);

    /// <summary>
    /// Draws the effect. The graphics object has an identity transform, so coordinates are window pixels.
    /// </summary>
    public abstract void Draw(Graphics g, EffectAnchors anchors);

    protected static float EaseOutCubic(float t)
    {
        float inv = 1f - t;
        return 1f - (inv * inv * inv);
    }

    protected static int ClampAlpha(float alpha)
    {
        return (int)Math.Max(0f, Math.Min(255f, alpha));
    }

    protected static float EaseInOutSine(float t)
    {
        return 0.5f - (0.5f * (float)Math.Cos(Math.PI * t));
    }

    // Overshoots past 1 and settles back, for a springy pop.
    protected static float EaseOutBack(float t, float overshoot = 1.7f)
    {
        float u = t - 1f;
        return 1f + (u * u * (((overshoot + 1f) * u) + overshoot));
    }

    // Rises from 0 to 1 and back to 0 smoothly over t = 0..1.
    protected static float Bell(float t)
    {
        return (float)Math.Sin(Math.PI * Math.Max(0f, Math.Min(1f, t)));
    }

    protected static float Clamp01(float value)
    {
        return Math.Max(0f, Math.Min(1f, value));
    }

    /// <summary>
    /// Mixes <paramref name="from"/> towards <paramref name="to"/>, keeping the alpha of <paramref name="from"/>.
    /// </summary>
    protected static Color Blend(Color from, Color to, float amount)
    {
        amount = Clamp01(amount);
        return Color.FromArgb(
            from.A,
            (int)(from.R + ((to.R - from.R) * amount)),
            (int)(from.G + ((to.G - from.G) * amount)),
            (int)(from.B + ((to.B - from.B) * amount)));
    }

    private static FontFamily techFontFamily;

    /// <summary>
    /// A monospace font for the Tech style.
    /// </summary>
    protected static FontFamily TechFontFamily => techFontFamily ??= CreateTechFontFamily();

    private static FontFamily CreateTechFontFamily()
    {
        try
        {
            return new FontFamily("Consolas");
        }
        catch (ArgumentException)
        {
            return FontFamily.GenericMonospace;
        }
    }

    /// <summary>
    /// Draws the four corners of <paramref name="rect"/> like a HUD target lock.
    /// </summary>
    protected static void DrawCornerBrackets(Graphics g, Pen pen, RectangleF rect, float arm)
    {
        arm = Math.Min(arm, Math.Min(rect.Width, rect.Height) / 2f);
        float l = rect.Left, t = rect.Top, r = rect.Right, b = rect.Bottom;
        g.DrawLines(pen, [new PointF(l, t + arm), new PointF(l, t), new PointF(l + arm, t)]);
        g.DrawLines(pen, [new PointF(r - arm, t), new PointF(r, t), new PointF(r, t + arm)]);
        g.DrawLines(pen, [new PointF(r, b - arm), new PointF(r, b), new PointF(r - arm, b)]);
        g.DrawLines(pen, [new PointF(l + arm, b), new PointF(l, b), new PointF(l, b - arm)]);
    }
}
