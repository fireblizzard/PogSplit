using LiveSplit.Options;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace LiveSplit.UI.Effects;

/// <summary>
/// Plays on a split row when that split lost a lot of time against the comparison: the row pulses red.
/// Bigger losses pulse stronger. Classic pulses twice, Chaotic strobes hard three times,
/// Chill fades a soft red glow in and out once, and Tech blinks a red alert with HUD brackets.
/// </summary>
public class LosingTimePulseEffect : Effect
{
    private readonly string _anchorKey;
    private readonly Color _color;
    private readonly EffectsStyle _style;
    private readonly float _strength;
    private readonly double _duration;

    /// <param name="severity">How bad the loss was, from 0 (just over the threshold) to 1 (far over it).</param>
    public LosingTimePulseEffect(int splitIndex, Color color, EffectsStyle style, float intensity, double duration, float severity)
        : base(splitIndex)
    {
        _anchorKey = EffectAnchors.SplitRow(splitIndex);
        _color = Color.FromArgb(255, color);
        _style = style;
        _strength = Clamp01(intensity) * (0.6f + (0.4f * Clamp01(severity)));
        _duration = Math.Max(0.1, duration);
    }

    public override double Duration => _duration;

    public override RectangleF GetBounds(EffectAnchors anchors)
    {
        if (!anchors.TryGet(_anchorKey, out RectangleF row) || row.Width < 1f || row.Height < 1f)
        {
            return RectangleF.Empty;
        }

        float pad = _style == EffectsStyle.Tech ? 4f : 2f;
        row.Inflate(pad, pad);
        return row;
    }

    public override void Draw(Graphics g, EffectAnchors anchors)
    {
        if (!anchors.TryGet(_anchorKey, out RectangleF row) || row.Width < 1f || row.Height < 1f)
        {
            return;
        }

        switch (_style)
        {
            case EffectsStyle.Chaotic:
                DrawPulses(g, row, 3, 170f, 255f, 1.5f, true);
                break;
            case EffectsStyle.Chill:
                DrawChill(g, row);
                break;
            case EffectsStyle.Tech:
                DrawTech(g, row);
                break;
            default:
                DrawPulses(g, row, 2, 110f, 220f, 1f, false);
                break;
        }
    }

    // A number of pulses that die down over the effect, each one a glow and a border.
    private void DrawPulses(Graphics g, RectangleF row, int count, float fillAlpha, float borderAlpha, float borderWidth, bool hot)
    {
        float t = Progress;
        float pulse = (float)Math.Abs(Math.Sin(t * Math.PI * count)) * (1f - (t * t));
        if (pulse <= 0f)
        {
            return;
        }

        // Chaotic flashes towards white at the peak of each pulse.
        Color color = hot ? Blend(_color, Color.White, 0.35f * pulse) : _color;
        using (var glow = new SolidBrush(Color.FromArgb(ClampAlpha(fillAlpha * pulse * _strength), color)))
        {
            g.FillRectangle(glow, row);
        }

        float inset = borderWidth / 2f;
        using var border = new Pen(Color.FromArgb(ClampAlpha(borderAlpha * pulse * _strength), color), borderWidth);
        g.DrawRectangle(border, row.X + inset, row.Y + inset, row.Width - borderWidth, row.Height - borderWidth);
    }

    // One slow, soft glow, brightest along the middle of the row.
    private void DrawChill(Graphics g, RectangleF row)
    {
        float glow = Bell(EaseInOutSine(Progress));
        if (glow <= 0f)
        {
            return;
        }

        var brushRect = RectangleF.Inflate(row, 0f, 1f);
        Color edge = Color.FromArgb(ClampAlpha(15f * glow * _strength), _color);
        Color middle = Color.FromArgb(ClampAlpha(65f * glow * _strength), _color);
        using var brush = new LinearGradientBrush(brushRect, edge, edge, LinearGradientMode.Vertical);
        brush.InterpolationColors = new ColorBlend
        {
            Colors = [edge, middle, edge],
            Positions = [0f, 0.5f, 1f]
        };
        g.FillRectangle(brush, row);
    }

    // A red alert: the row blinks on and off three times behind scanlines, framed by HUD brackets.
    private void DrawTech(Graphics g, RectangleF row)
    {
        float t = Progress;
        float fadeOut = t < 0.75f ? 1f : 1f - ((t - 0.75f) / 0.25f);
        bool on = (int)(t * 6f) % 2 == 0;

        if (on)
        {
            using (var tint = new SolidBrush(Color.FromArgb(ClampAlpha(90f * fadeOut * _strength), _color)))
            {
                g.FillRectangle(tint, row);
            }

            using var lines = new Pen(Color.FromArgb(ClampAlpha(80f * fadeOut * _strength), Blend(_color, Color.Black, 0.5f)), 1f);
            float spacing = Math.Max(3f, row.Height / 8f);
            for (float y = row.Top + (spacing / 2f); y < row.Bottom; y += spacing)
            {
                g.DrawLine(lines, row.Left, y, row.Right, y);
            }
        }

        using var pen = new Pen(Color.FromArgb(ClampAlpha(255f * fadeOut * _strength), _color), 1.5f);
        DrawCornerBrackets(g, pen, RectangleF.Inflate(row, 2f, 2f), Math.Max(4f, row.Height * 0.45f));
    }
}
