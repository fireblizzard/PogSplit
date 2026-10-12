using LiveSplit.Options;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;

namespace LiveSplit.UI.Effects;

/// <summary>
/// Plays on a split row when that split was a best segment, like a damage number in an RPG:
/// the time saved (for example "-3.4") pops up over the row, drifts while growing, and fades out.
/// Chaotic makes it spring in and wobble, Chill floats it softly, and Tech decodes it digit by digit.
/// </summary>
public class TimeSaveNumberEffect : Effect
{
    // Share of the duration spent growing in.
    private const float PopPortion = 0.2f;

    // How often the jitter and the decoding digits change, in seconds.
    private const double GlitchFrameSeconds = 0.045;

    private readonly string _anchorKey;
    private readonly string _text;
    private readonly Color _color;
    private readonly Color _accent;
    private readonly FontFamily _fontFamily;
    private readonly EffectsStyle _style;
    private readonly float _intensity;
    private readonly double _duration;
    private readonly float _fadeStart;
    private readonly float _maxRotation;
    private readonly float _drift;
    private readonly float _textSize;
    private readonly float _popScale;
    private readonly int _seed = Environment.TickCount;
    private RectangleF _row;

    public TimeSaveNumberEffect(int splitIndex, TimeSpan timeSaved, Color color, FontFamily fontFamily, LayoutSettings settings)
        : base(splitIndex)
    {
        _anchorKey = EffectAnchors.SplitRow(splitIndex);
        _text = FormatTimeSave(timeSaved);
        _color = color;
        _accent = settings.EffectsAccentColor;
        _style = settings.EffectsStyle;
        _fontFamily = _style == EffectsStyle.Tech ? TechFontFamily : fontFamily;
        _intensity = Clamp01(settings.EffectsIntensity);
        _duration = Math.Max(0.1, settings.TimeSaveDuration);
        _fadeStart = Math.Max(0f, Math.Min(0.99f, settings.TimeSaveFadeStart));
        _maxRotation = settings.TimeSaveRotationDirection switch
        {
            RotationDirection.Anticlockwise => -settings.TimeSaveRotation,
            RotationDirection.Random => PickRandomRotation(Math.Abs(settings.TimeSaveRotation)),
            _ => settings.TimeSaveRotation
        };
        _drift = settings.TimeSaveDrift;
        _textSize = Math.Max(0.1f, settings.TimeSaveTextSize);
        _popScale = Math.Max(0.1f, settings.TimeSavePopScale);
    }

    public override double Duration => _duration;

    // Either direction, by anything from none up to the limit.
    private static float PickRandomRotation(float limit)
    {
        var random = new Random();
        float amount = (float)(random.NextDouble() * limit);
        return random.Next(2) == 0 ? amount : -amount;
    }

    /// <summary>
    /// Formats a time save as "-3.4", "-0.07" or "-1:02.3".
    /// </summary>
    public static string FormatTimeSave(TimeSpan timeSaved)
    {
        TimeSpan saved = timeSaved.Duration();
        if (saved.TotalMinutes >= 1)
        {
            return "-" + ((int)saved.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":" + saved.ToString(@"ss\.f", CultureInfo.InvariantCulture);
        }

        string format = saved.TotalSeconds < 0.1 ? "0.00" : "0.0";
        return "-" + saved.TotalSeconds.ToString(format, CultureInfo.InvariantCulture);
    }

    public override RectangleF GetBounds(EffectAnchors anchors)
    {
        using GraphicsPath path = CreateTextPath(anchors);
        if (path == null)
        {
            return RectangleF.Empty;
        }

        RectangleF bounds = path.GetBounds();
        float pad = OutlineWidth() + 2f;
        if (_style == EffectsStyle.Tech)
        {
            // Room for the color split and the underline.
            bounds.Inflate(ColorSplitOffset() + pad, pad + (_row.Height * 0.25f));
        }
        else
        {
            bounds.Inflate(pad, pad + (_style == EffectsStyle.Chill ? _row.Height * 0.08f : 0f));
        }

        return bounds;
    }

    public override void Draw(Graphics g, EffectAnchors anchors)
    {
        using GraphicsPath path = CreateTextPath(anchors);
        if (path == null)
        {
            return;
        }

        float alpha = Opacity() * (0.5f + (0.5f * _intensity));
        switch (_style)
        {
            case EffectsStyle.Chill:
                DrawChill(g, path, alpha);
                break;
            case EffectsStyle.Tech:
                DrawTech(g, path, alpha);
                break;
            default:
                DrawOutlined(g, path, alpha, _style == EffectsStyle.Chaotic && Progress < 0.12f ? Color.White : _color);
                break;
        }
    }

    private void DrawOutlined(Graphics g, GraphicsPath path, float alpha, Color color)
    {
        using (var outline = new Pen(Color.FromArgb(ClampAlpha(200f * alpha), Color.Black), OutlineWidth()) { LineJoin = LineJoin.Round })
        {
            g.DrawPath(outline, path);
        }

        using var fill = new SolidBrush(Color.FromArgb(ClampAlpha(255f * alpha), color));
        g.FillPath(fill, path);
    }

    // A soft drop shadow instead of a hard outline, in a lighter gold.
    private void DrawChill(Graphics g, GraphicsPath path, float alpha)
    {
        float offset = Math.Max(1f, _row.Height * 0.05f);
        using (var shadowPath = (GraphicsPath)path.Clone())
        using (var move = new Matrix())
        {
            move.Translate(0f, offset);
            shadowPath.Transform(move);
            using var shadow = new SolidBrush(Color.FromArgb(ClampAlpha(90f * alpha), Color.Black));
            g.FillPath(shadow, shadowPath);
        }

        using var fill = new SolidBrush(Color.FromArgb(ClampAlpha(255f * alpha), Blend(_color, Color.White, 0.3f)));
        g.FillPath(fill, path);
    }

    // Red and cyan ghost copies that snap together, then the number in the accent color with an underline.
    private void DrawTech(Graphics g, GraphicsPath path, float alpha)
    {
        float split = ColorSplitOffset();
        if (split > 0.5f)
        {
            DrawShifted(g, path, -split, Color.FromArgb(ClampAlpha(170f * alpha), 255, 40, 90));
            DrawShifted(g, path, split, Color.FromArgb(ClampAlpha(170f * alpha), 40, 200, 255));
        }

        DrawOutlined(g, path, alpha, _accent);

        RectangleF bounds = path.GetBounds();
        float grow = EaseOutCubic(Clamp01(Progress / 0.3f));
        float y = bounds.Bottom + Math.Max(2f, _row.Height * 0.1f);
        float tick = Math.Max(2f, _row.Height * 0.12f);
        float right = bounds.Left + (bounds.Width * grow);
        using var pen = new Pen(Color.FromArgb(ClampAlpha(230f * alpha), _accent), Math.Max(1f, _row.Height * 0.05f));
        g.DrawLine(pen, bounds.Left, y, right, y);
        g.DrawLine(pen, bounds.Left, y, bounds.Left, y - tick);
        if (grow >= 1f)
        {
            g.DrawLine(pen, right, y, right, y - tick);
        }
    }

    private static void DrawShifted(Graphics g, GraphicsPath path, float dx, Color color)
    {
        using var shifted = (GraphicsPath)path.Clone();
        using (var move = new Matrix())
        {
            move.Translate(dx, 0f);
            shifted.Transform(move);
        }

        using var brush = new SolidBrush(color);
        g.FillPath(brush, shifted);
    }

    private float Opacity()
    {
        float t = Progress;
        float fadeOut = t < _fadeStart ? 1f : Math.Max(0f, 1f - ((t - _fadeStart) / (1f - _fadeStart)));
        switch (_style)
        {
            case EffectsStyle.Chill:
                return EaseInOutSine(Clamp01(t / 0.15f)) * EaseInOutSine(fadeOut);
            case EffectsStyle.Tech:
                // Flickers while it fades, like a failing hologram.
                return fadeOut < 1f && GlitchFrame() % 2 == 1 ? fadeOut * 0.55f : fadeOut;
            default:
                return fadeOut;
        }
    }

    // Builds the number's outline at its current position and size, or null if its split row isn't known.
    private GraphicsPath CreateTextPath(EffectAnchors anchors)
    {
        if (anchors.TryGet(_anchorKey, out RectangleF row) && row.Width >= 1f && row.Height >= 1f)
        {
            // Follows the row while it is visible, and stays put if it scrolls away.
            _row = row;
        }

        if (_row.IsEmpty)
        {
            return null;
        }

        float t = Progress;
        float scale, drift, rotation;
        var jitter = PointF.Empty;
        switch (_style)
        {
            case EffectsStyle.Chaotic:
                // Springs in from tiny with a big overshoot, wobbles, shakes, and keeps swelling.
                scale = t < 0.25f
                    ? _popScale * (0.3f + (0.7f * EaseOutBack(t / 0.25f, 3f)))
                    : _popScale * (1f + (0.2f * ((t - 0.25f) / 0.75f)));
                drift = _row.Height * _drift * EaseOutCubic(t);
                rotation = (_maxRotation * EaseOutCubic(t)) + (12f * (1f - t) * (float)Math.Sin(t * Math.PI * 10.0));
                if (t < 0.35f)
                {
                    var random = new Random(_seed + GlitchFrame());
                    float amount = _row.Height * 0.07f * (1f - (t / 0.35f));
                    jitter = new PointF((float)((random.NextDouble() * 2.0) - 1.0) * amount, (float)((random.NextDouble() * 2.0) - 1.0) * amount);
                }

                break;
            case EffectsStyle.Chill:
                scale = _popScale * (0.85f + (0.15f * EaseInOutSine(Clamp01(t / 0.3f))));
                drift = _row.Height * _drift * EaseInOutSine(t);
                rotation = _maxRotation * EaseInOutSine(t);
                break;
            case EffectsStyle.Tech:
                scale = t < 0.08f ? _popScale * (0.6f + (0.4f * (t / 0.08f))) : _popScale;
                drift = _row.Height * _drift * EaseOutCubic(t);
                rotation = _maxRotation * EaseOutCubic(t);
                break;
            default:
                // Pops in from small to slightly oversized, then keeps growing slowly while it drifts down.
                float startScale = _popScale * 0.48f;
                scale = t < PopPortion
                    ? startScale + ((_popScale - startScale) * EaseOutCubic(t / PopPortion))
                    : _popScale * (1f + (0.12f * ((t - PopPortion) / (1f - PopPortion))));
                drift = _row.Height * _drift * EaseOutCubic(t);
                rotation = _maxRotation * EaseOutCubic(t);
                break;
        }

        float emSize = _row.Height * _textSize;
        var path = new GraphicsPath();
        using (var format = new StringFormat(StringFormat.GenericTypographic))
        {
            path.AddString(CurrentText(), _fontFamily, (int)FontStyle.Bold, emSize, PointF.Empty, format);
        }

        RectangleF textBounds = path.GetBounds();
        var center = new PointF(_row.Left + (_row.Width / 2f) + jitter.X, _row.Top + (_row.Height * 0.6f) + drift + jitter.Y);
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

    // Tech decodes the number: digits cycle randomly and lock in from left to right.
    private string CurrentText()
    {
        if (_style != EffectsStyle.Tech)
        {
            return _text;
        }

        int locked = (int)(_text.Length * Clamp01(Progress / 0.4f));
        if (locked >= _text.Length)
        {
            return _text;
        }

        var random = new Random(_seed + GlitchFrame());
        var builder = new StringBuilder(_text);
        for (int i = locked; i < builder.Length; i++)
        {
            if (char.IsDigit(builder[i]))
            {
                builder[i] = (char)('0' + random.Next(10));
            }
        }

        return builder.ToString();
    }

    private int GlitchFrame()
    {
        return (int)(Age / GlitchFrameSeconds);
    }

    private float ColorSplitOffset()
    {
        float t = Progress;
        return t >= 0.35f ? 0f : _row.Height * 0.1f * (1f - (t / 0.35f));
    }

    private float OutlineWidth()
    {
        return _style switch
        {
            EffectsStyle.Chaotic => Math.Max(2.5f, _row.Height * 0.16f),
            EffectsStyle.Tech => Math.Max(1.5f, _row.Height * 0.08f),
            _ => Math.Max(2f, _row.Height * 0.12f)
        };
    }
}
