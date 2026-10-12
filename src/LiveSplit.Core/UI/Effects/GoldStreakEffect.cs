using LiveSplit.Options;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace LiveSplit.UI.Effects;

/// <summary>
/// Plays on a split row when it is the second or later best segment in a row, showing the streak like a combo counter.
/// Chaotic makes it bigger, louder and rainbow-colored as the streak grows, Chill keeps it small and quiet,
/// and Tech shows it as a HUD readout.
/// </summary>
public class GoldStreakEffect : Effect
{
    private readonly string _anchorKey;
    private readonly string _text;
    private readonly int _streak;
    private readonly Color _color;
    private readonly Color _accent;
    private readonly FontFamily _fontFamily;
    private readonly EffectsStyle _style;
    private readonly float _intensity;
    private readonly double _duration;
    private RectangleF _row;

    public GoldStreakEffect(int splitIndex, int streak, Color color, FontFamily fontFamily, LayoutSettings settings)
        : base(splitIndex)
    {
        _anchorKey = EffectAnchors.SplitRow(splitIndex);
        _streak = streak;
        _color = color;
        _accent = settings.EffectsAccentColor;
        _style = settings.EffectsStyle;
        _intensity = Clamp01(settings.EffectsIntensity);
        _duration = Math.Max(0.3, settings.TimeSaveDuration * 1.3);
        _fontFamily = _style == EffectsStyle.Tech ? TechFontFamily : fontFamily;
        string count = streak.ToString(CultureInfo.InvariantCulture);
        _text = _style switch
        {
            EffectsStyle.Chaotic => "x" + count + new string('!', Math.Min(streak - 1, 4)),
            EffectsStyle.Chill => count + " in a row",
            EffectsStyle.Tech => "STREAK " + streak.ToString("00", CultureInfo.InvariantCulture),
            _ => "x" + count
        };
    }

    public override double Duration => _duration;

    public override RectangleF GetBounds(EffectAnchors anchors)
    {
        using GraphicsPath path = CreateTextPath(anchors);
        if (path == null)
        {
            return RectangleF.Empty;
        }

        RectangleF bounds = path.GetBounds();
        float pad = OutlineWidth() + 2f + (_style == EffectsStyle.Tech ? _row.Height * 0.3f : 0f);
        bounds.Inflate(pad, pad);
        return bounds;
    }

    public override void Draw(Graphics g, EffectAnchors anchors)
    {
        using GraphicsPath path = CreateTextPath(anchors);
        if (path == null)
        {
            return;
        }

        float t = Progress;
        float alpha = _style == EffectsStyle.Chill
            ? Bell(EaseInOutSine(t))
            : t < 0.7f ? 1f : 1f - ((t - 0.7f) / 0.3f);
        alpha *= 0.5f + (0.5f * _intensity);

        Color color = _style switch
        {
            EffectsStyle.Chaotic => _streak >= 4 ? Rainbow((float)Age * 3f) : Blend(_color, Color.White, 0.5f * Bell(t * 4f)),
            EffectsStyle.Chill => Blend(_color, Color.White, 0.4f),
            EffectsStyle.Tech => _accent,
            _ => _color
        };

        if (_style == EffectsStyle.Tech)
        {
            RectangleF plate = path.GetBounds();
            plate.Inflate(_row.Height * 0.2f, _row.Height * 0.15f);
            using (var backing = new SolidBrush(Color.FromArgb(ClampAlpha(140f * alpha), Color.Black)))
            {
                g.FillRectangle(backing, plate);
            }

            using var pen = new Pen(Color.FromArgb(ClampAlpha(255f * alpha), _accent), 1.5f);
            DrawCornerBrackets(g, pen, plate, _row.Height * 0.25f);
        }
        else if (_style != EffectsStyle.Chill)
        {
            using var outline = new Pen(Color.FromArgb(ClampAlpha(200f * alpha), Color.Black), OutlineWidth()) { LineJoin = LineJoin.Round };
            g.DrawPath(outline, path);
        }

        using var fill = new SolidBrush(Color.FromArgb(ClampAlpha(255f * alpha), color));
        g.FillPath(fill, path);
    }

    // The counter sits towards the right of the row, a little above it, so it doesn't cover the time saved number.
    private GraphicsPath CreateTextPath(EffectAnchors anchors)
    {
        if (anchors.TryGet(_anchorKey, out RectangleF row) && row.Width >= 1f && row.Height >= 1f)
        {
            _row = row;
        }

        if (_row.IsEmpty)
        {
            return null;
        }

        float t = Progress;
        float emSize = _row.Height * 0.55f;
        float scale;
        float rotation = 0f;
        float rise;
        switch (_style)
        {
            case EffectsStyle.Chaotic:
                // Bigger for longer streaks, slams in and tilts.
                float size = 1f + (0.15f * Math.Min(_streak - 2, 6));
                scale = size * (t < 0.2f ? 0.2f + (0.8f * EaseOutBack(t / 0.2f, 3.5f)) : 1f + (0.1f * (t - 0.2f)));
                rotation = -10f + (6f * (float)Math.Sin(Age * 18.0) * (1f - t));
                rise = _row.Height * 0.4f * EaseOutCubic(t);
                break;
            case EffectsStyle.Chill:
                emSize = _row.Height * 0.4f;
                scale = 1f;
                rise = _row.Height * 0.5f * EaseInOutSine(t);
                break;
            case EffectsStyle.Tech:
                emSize = _row.Height * 0.4f;
                scale = t < 0.08f ? 0.6f + (0.4f * (t / 0.08f)) : 1f;
                rise = 0f;
                break;
            default:
                scale = t < 0.2f ? 0.5f + (0.5f * EaseOutCubic(t / 0.2f)) : 1f;
                rise = _row.Height * 0.3f * EaseOutCubic(t);
                break;
        }

        var path = new GraphicsPath();
        using (var format = new StringFormat(StringFormat.GenericTypographic))
        {
            path.AddString(_text, _fontFamily, (int)FontStyle.Bold, emSize, PointF.Empty, format);
        }

        RectangleF textBounds = path.GetBounds();
        float halfWidth = textBounds.Width * scale / 2f;
        var center = new PointF(
            Math.Max(_row.Left + halfWidth, _row.Right - halfWidth - (_row.Height * 0.3f)),
            _row.Top - rise);
        using (var transform = new Matrix())
        {
            transform.Translate(center.X, center.Y);
            transform.Rotate(rotation);
            transform.Scale(scale, scale);
            transform.Translate(-(textBounds.Left + (textBounds.Width / 2f)), -(textBounds.Top + (textBounds.Height / 2f)));
            path.Transform(transform);
        }

        return path;
    }

    private float OutlineWidth()
    {
        return Math.Max(2f, _row.Height * 0.1f);
    }

    private static Color Rainbow(float hue)
    {
        double h = (hue % 1f + 1f) % 1f * 6.0;
        int sector = (int)h;
        float f = (float)(h - sector);
        int up = (int)(255 * f), down = (int)(255 * (1f - f));
        return sector switch
        {
            0 => Color.FromArgb(255, up, 0),
            1 => Color.FromArgb(down, 255, 0),
            2 => Color.FromArgb(0, 255, up),
            3 => Color.FromArgb(0, down, 255),
            4 => Color.FromArgb(up, 0, 255),
            _ => Color.FromArgb(255, 0, down)
        };
    }
}
