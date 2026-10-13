using LiveSplit.Options;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace LiveSplit.UI.Effects;

/// <summary>
/// Plays on a split row when that split was a best segment. How it looks depends on the <see cref="EffectsStyle"/>:
/// Classic sweeps a band of gold light across a glowing row, Chaotic strobes and throws out a shockwave border,
/// Chill fades a soft glow in and out, and Tech locks HUD brackets onto the row and scans it.
/// </summary>
public class GoldSplitFlashEffect : Effect
{
    // Share of the effect's duration the sweep takes; the glow keeps fading after it.
    private const float SweepPortion = 0.6f;

    private readonly string _anchorKey;
    private readonly Color _color;
    private readonly Color _accent;
    private readonly EffectsStyle _style;
    private readonly float _intensity;
    private readonly double _duration;

    public GoldSplitFlashEffect(int splitIndex, Color color, Color accent, EffectsStyle style, float intensity, double duration)
        : base(splitIndex)
    {
        _duration = Math.Max(0.1, duration);
        _anchorKey = EffectAnchors.SplitRow(splitIndex);
        _color = color;
        _accent = accent;
        _style = style;
        _intensity = Clamp01(intensity);
    }

    public override double Duration => _duration;

    public override RectangleF GetBounds(EffectAnchors anchors)
    {
        if (!anchors.TryGet(_anchorKey, out RectangleF row) || row.Width < 1f || row.Height < 1f)
        {
            return RectangleF.Empty;
        }

        float pad = _style switch
        {
            EffectsStyle.Chaotic => ShockwaveReach(row) + 3f,
            EffectsStyle.Tech => BracketReach(row) + 3f,
            _ => 2f
        };
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
                DrawChaotic(g, row);
                break;
            case EffectsStyle.Chill:
                DrawChill(g, row);
                break;
            case EffectsStyle.Tech:
                DrawTech(g, row);
                break;
            default:
                DrawClassic(g, row);
                break;
        }
    }

    private void DrawClassic(Graphics g, RectangleF row)
    {
        float t = Progress;
        float fade = (1f - t) * (1f - t);

        using (var glow = new SolidBrush(Color.FromArgb(ClampAlpha(80f * fade * _intensity), _color)))
        {
            g.FillRectangle(glow, row);
        }

        float sweep = t / SweepPortion;
        if (sweep < 1f)
        {
            DrawSweep(g, row, EaseOutCubic(sweep), 0.35f, 170f, _color);
        }

        using var border = new Pen(Color.FromArgb(ClampAlpha(220f * fade * _intensity), _color), 1f);
        g.DrawRectangle(border, row.X + 0.5f, row.Y + 0.5f, row.Width - 1f, row.Height - 1f);
    }

    // Three hard strobes, a white-hot double sweep and a border that blasts outward.
    private void DrawChaotic(Graphics g, RectangleF row)
    {
        float t = Progress;
        float fade = 1f - t;
        float strobe = 0.35f + (0.65f * (float)Math.Abs(Math.Cos(t * Math.PI * 3.0)));
        Color hot = Blend(_color, Color.White, 0.5f * strobe * fade);

        using (var glow = new SolidBrush(Color.FromArgb(ClampAlpha(140f * fade * strobe * _intensity), hot)))
        {
            g.FillRectangle(glow, row);
        }

        float sweep = t / 0.45f;
        if (sweep < 1f)
        {
            DrawSweep(g, row, EaseOutCubic(sweep), 0.25f, 230f, Color.White);
            DrawSweep(g, row, 1f - EaseOutCubic(sweep), 0.25f, 200f, _color);
        }

        using (var border = new Pen(Color.FromArgb(ClampAlpha(255f * fade * _intensity), hot), 1.5f))
        {
            g.DrawRectangle(border, row.X + 0.75f, row.Y + 0.75f, row.Width - 1.5f, row.Height - 1.5f);
        }

        // A shockwave border that flies outward and thins out.
        float wave = Clamp01(t / 0.5f);
        if (wave < 1f)
        {
            float reach = ShockwaveReach(row) * EaseOutCubic(wave);
            RectangleF shock = RectangleF.Inflate(row, reach, reach);
            using var pen = new Pen(Color.FromArgb(ClampAlpha(230f * (1f - wave) * _intensity), _color), Math.Max(1f, 2.5f * (1f - wave)));
            g.DrawRectangle(pen, shock.X, shock.Y, shock.Width, shock.Height);
        }
    }

    // A soft glow that breathes in and out, brightest along the middle of the row.
    private void DrawChill(Graphics g, RectangleF row)
    {
        float glow = Bell(EaseInOutSine(Progress));
        if (glow <= 0f)
        {
            return;
        }

        var brushRect = RectangleF.Inflate(row, 0f, 1f);
        Color edge = Color.FromArgb(ClampAlpha(20f * glow * _intensity), _color);
        Color middle = Color.FromArgb(ClampAlpha(75f * glow * _intensity), _color);
        using (var brush = new LinearGradientBrush(brushRect, edge, edge, LinearGradientMode.Vertical))
        {
            brush.InterpolationColors = new ColorBlend
            {
                Colors = [edge, middle, edge],
                Positions = [0f, 0.5f, 1f]
            };
            g.FillRectangle(brush, row);
        }

        // A faint underline that grows from the middle out.
        float width = row.Width * 0.8f * EaseInOutSine(Clamp01(Progress * 2f));
        using var line = new Pen(Color.FromArgb(ClampAlpha(120f * glow * _intensity), _color), 1f);
        float y = row.Bottom - 1.5f;
        float centerX = row.Left + (row.Width / 2f);
        g.DrawLine(line, centerX - (width / 2f), y, centerX + (width / 2f), y);
    }

    // HUD brackets lock onto the row, a scan beam passes over it and scanlines flicker behind it.
    private void DrawTech(Graphics g, RectangleF row)
    {
        float t = Progress;
        float fadeOut = t < 0.75f ? 1f : 1f - ((t - 0.75f) / 0.25f);

        // Scanlines over the scanned part of the row.
        float scan = Clamp01(t / 0.5f);
        float scannedRight = row.Left + (row.Width * EaseInOutSine(scan));
        Region oldClip = g.Clip;
        g.SetClip(RectangleF.FromLTRB(row.Left, row.Top, scannedRight, row.Bottom), CombineMode.Intersect);
        using (var lines = new Pen(Color.FromArgb(ClampAlpha(70f * fadeOut * _intensity), _accent), 1f))
        {
            float spacing = Math.Max(3f, row.Height / 8f);
            for (float y = row.Top + (spacing / 2f); y < row.Bottom; y += spacing)
            {
                g.DrawLine(lines, row.Left, y, row.Right, y);
            }
        }

        using (var tint = new SolidBrush(Color.FromArgb(ClampAlpha(40f * fadeOut * _intensity), _accent)))
        {
            g.FillRectangle(tint, row);
        }

        g.Clip = oldClip;
        oldClip.Dispose();

        // The scan beam: a bright vertical line with a fading trail.
        if (scan < 1f)
        {
            DrawSweep(g, row, scan, 0.18f, 160f, _accent);
            using var beam = new Pen(Color.FromArgb(ClampAlpha(255f * _intensity), Blend(_accent, Color.White, 0.6f)), 1.5f);
            g.DrawLine(beam, scannedRight, row.Top, scannedRight, row.Bottom);
        }

        // Brackets fly in from outside the row and blink twice when they lock on.
        float lockIn = Clamp01(t / 0.3f);
        float reach = BracketReach(row) * (1f - EaseOutCubic(lockIn));
        RectangleF target = RectangleF.Inflate(row, reach + 1f, reach + 1f);
        bool blinkOff = lockIn >= 1f && t < 0.42f && ((int)((t - 0.3f) / 0.03f) % 2 == 1);
        if (!blinkOff)
        {
            float bracketAlpha = (lockIn < 1f ? 0.4f + (0.6f * lockIn) : 1f) * fadeOut;
            using var pen = new Pen(Color.FromArgb(ClampAlpha(255f * bracketAlpha * _intensity), _accent), 1.5f);
            DrawCornerBrackets(g, pen, target, Math.Max(4f, row.Height * 0.45f));
        }
    }

    private void DrawSweep(Graphics g, RectangleF row, float position, float widthPortion, float peakAlpha, Color color)
    {
        float bandWidth = Math.Max(row.Width * widthPortion, 8f);
        float x = row.Left - bandWidth + ((row.Width + bandWidth) * position);
        var band = new RectangleF(x, row.Top, bandWidth, row.Height);

        // The brush rectangle is padded so the gradient doesn't wrap at the band's edges.
        var brushRect = RectangleF.Inflate(band, 1f, 0f);
        Color edge = Color.FromArgb(0, color);
        using var brush = new LinearGradientBrush(brushRect, edge, edge, LinearGradientMode.Horizontal);
        brush.InterpolationColors = new ColorBlend
        {
            Colors = [edge, Color.FromArgb(ClampAlpha(peakAlpha * _intensity), color), edge],
            Positions = [0f, 0.5f, 1f]
        };

        Region oldClip = g.Clip;
        g.SetClip(row, CombineMode.Intersect);
        g.FillRectangle(brush, band);
        g.Clip = oldClip;
        oldClip.Dispose();
    }

    private static float ShockwaveReach(RectangleF row)
    {
        return row.Height * 0.6f;
    }

    private static float BracketReach(RectangleF row)
    {
        return row.Height * 0.8f;
    }
}
