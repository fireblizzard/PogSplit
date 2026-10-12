using System;
using System.Drawing;

namespace LiveSplit.UI.Effects;

/// <summary>
/// Shakes the whole layout for a moment. It draws nothing itself:
/// the timer window moves everything it paints by <see cref="Offset"/> while the shake plays.
/// </summary>
public class ScreenShakeEffect : Effect
{
    private readonly float _strength;
    private readonly double _duration;
    private readonly float _phase;

    public ScreenShakeEffect(int splitIndex, float strength, double duration)
        : base(splitIndex)
    {
        _strength = Math.Max(0f, strength);
        _duration = Math.Max(0.05, duration);
        _phase = (float)(new Random().NextDouble() * Math.PI * 2);
    }

    public override double Duration => _duration;

    /// <summary>
    /// How far the layout is pushed right now, in window pixels. Starts strong and dies down.
    /// </summary>
    public PointF Offset
    {
        get
        {
            float t = Progress;
            float amplitude = _strength * (1f - t) * (1f - t);
            double age = Age;
            return new PointF(
                amplitude * (float)Math.Sin((age * 97.0) + _phase),
                amplitude * (float)Math.Cos((age * 71.0) + (_phase * 1.7)));
        }
    }

    public override RectangleF GetBounds(EffectAnchors anchors)
    {
        // The whole window repaints while it shakes.
        return anchors.TryGet(EffectAnchors.Window, out RectangleF window) ? window : RectangleF.Empty;
    }

    public override void Draw(Graphics g, EffectAnchors anchors)
    {
    }
}
