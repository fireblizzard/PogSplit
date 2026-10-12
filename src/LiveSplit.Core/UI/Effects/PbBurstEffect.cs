using LiveSplit.Options;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace LiveSplit.UI.Effects;

/// <summary>
/// Plays when a run finishes with a new personal best. How it looks depends on the <see cref="EffectsStyle"/>:
/// Classic bursts a fountain of particles out of the timer, Chaotic fires three waves of confetti after a white flash,
/// Chill blooms a soft light with slowly rising sparkles, and Tech sends grid ripples across the window under a "NEW RECORD" HUD label.
/// </summary>
public class PbBurstEffect : Effect
{
    private const int MaxParticles = 160;
    private const float RingSeconds = 0.4f;
    private const float FlashSeconds = 0.25f;
    private const string RecordLabel = "NEW RECORD";

    private struct Particle
    {
        public float X;
        public float Y;
        public float VelocityX;
        public float VelocityY;
        public float Size;
        public float Life;
        public float Delay;
        public float Rotation;
        public float Spin;
        public float Phase;
        public int ColorIndex;
    }

    private readonly Particle[] _particles;
    private readonly Color[] _colors;
    private readonly Color _accent;
    private readonly EffectsStyle _style;
    private readonly float _intensity;
    private readonly Random _random = new();
    private readonly SolidBrush _brush = new(Color.White);
    private readonly double _duration;

    private bool _started;
    private PointF _origin;
    private RectangleF _timerArea;
    private RectangleF _window;
    private float _scale;
    private float _gravity;
    private float _drag;
    private RectangleF _bounds;

    public PbBurstEffect(int lastSplitIndex, Color goldColor, Color personalBestColor, LayoutSettings settings)
        : base(lastSplitIndex)
    {
        _style = settings.EffectsStyle;
        _accent = settings.EffectsAccentColor;
        _duration = Math.Max(0.5, settings.PbBurstDuration);
        _intensity = Clamp01(settings.EffectsIntensity);

        int count;
        switch (_style)
        {
            case EffectsStyle.Chaotic:
                _colors = [goldColor, personalBestColor, Color.White, Color.FromArgb(255, 60, 200), Color.FromArgb(0, 230, 255), Color.FromArgb(120, 255, 60), Color.FromArgb(255, 150, 0)];
                count = (int)(120 + (200 * _intensity));
                break;
            case EffectsStyle.Chill:
                _colors = [Blend(goldColor, Color.White, 0.35f), Blend(personalBestColor, Color.White, 0.35f), Color.White];
                count = (int)(24 + (40 * _intensity));
                break;
            case EffectsStyle.Tech:
                _colors = [_accent, Blend(_accent, Color.White, 0.6f)];
                count = (int)(16 + (32 * _intensity));
                break;
            default:
                _colors = [goldColor, personalBestColor, Color.White];
                count = (int)(40 + ((MaxParticles - 40) * _intensity));
                break;
        }

        _particles = new Particle[count];
    }

    public override double Duration => _duration;

    public override bool Advance(double deltaSeconds)
    {
        if (!_started)
        {
            // Started on the first tick, once the timer has reported where it is on screen.
            return false;
        }

        if (!base.Advance(deltaSeconds))
        {
            return false;
        }

        float dt = (float)deltaSeconds;
        float age = (float)Age;
        float drag = (float)Math.Exp(-_drag * dt);
        float left = _origin.X, top = _origin.Y, right = _origin.X, bottom = _origin.Y;
        for (int i = 0; i < _particles.Length; i++)
        {
            ref Particle p = ref _particles[i];
            if (age < p.Delay)
            {
                continue;
            }

            p.VelocityX *= drag;
            p.VelocityY = (p.VelocityY * drag) + (_gravity * dt);
            p.X += p.VelocityX * dt;
            p.Y += p.VelocityY * dt;
            p.Rotation += p.Spin * dt;

            float reach = (p.Size * 2f) + SwayAmount();
            left = Math.Min(left, p.X - reach);
            top = Math.Min(top, p.Y - reach);
            right = Math.Max(right, p.X + reach);
            bottom = Math.Max(bottom, p.Y + reach);
        }

        float ring = _style == EffectsStyle.Chill ? BloomRadius() : RingRadius(0f);
        left = Math.Min(left, _origin.X - ring);
        top = Math.Min(top, _origin.Y - ring);
        right = Math.Max(right, _origin.X + ring);
        bottom = Math.Max(bottom, _origin.Y + ring);

        // Chaotic flashes and Tech ripples cover the whole window.
        _bounds = _style is EffectsStyle.Chaotic or EffectsStyle.Tech
            ? RectangleF.Union(_window, RectangleF.FromLTRB(left, top, right, bottom))
            : RectangleF.FromLTRB(left - 2f, top - 2f, right + 2f, bottom + 2f);
        return true;
    }

    /// <summary>
    /// Places the particles around the timer, or the middle of the window if there is no timer.
    /// </summary>
    /// <returns>False if neither is known yet.</returns>
    public bool TryStart(EffectAnchors anchors)
    {
        if (_started)
        {
            return true;
        }

        bool hasWindow = anchors.TryGet(EffectAnchors.Window, out _window);
        if (!anchors.TryGet(EffectAnchors.Timer, out RectangleF area))
        {
            if (!hasWindow)
            {
                return false;
            }

            area = _window;
        }

        if (!hasWindow)
        {
            _window = area;
        }

        _timerArea = area;
        _origin = new PointF(area.Left + (area.Width / 2f), area.Top + (area.Height / 2f));
        _scale = Math.Max(Math.Max(area.Width, area.Height), 150f);
        float sizeScale = Math.Max(0.8f, Math.Min(2f, area.Height / 60f));

        switch (_style)
        {
            case EffectsStyle.Chaotic:
                StartConfetti(area, sizeScale);
                break;
            case EffectsStyle.Chill:
                StartSparkles(area, sizeScale);
                break;
            case EffectsStyle.Tech:
                StartDataBits(sizeScale);
                break;
            default:
                StartFountain(area, sizeScale);
                break;
        }

        _bounds = RectangleF.Empty;
        _started = true;
        return true;
    }

    private void StartFountain(RectangleF area, float sizeScale)
    {
        _gravity = _scale * 2.4f;
        _drag = 1.6f;
        for (int i = 0; i < _particles.Length; i++)
        {
            // Mostly upward, spreading to the sides like a fountain.
            double angle = (-Math.PI / 2) + ((_random.NextDouble() - 0.5) * Math.PI * 1.3);
            float speed = _scale * (float)(0.6 + (_random.NextDouble() * 0.8));
            _particles[i] = new Particle
            {
                X = _origin.X + (float)((_random.NextDouble() - 0.5) * area.Width * 0.6),
                Y = _origin.Y,
                VelocityX = (float)Math.Cos(angle) * speed,
                VelocityY = (float)Math.Sin(angle) * speed,
                Size = sizeScale * (float)(1.5 + (_random.NextDouble() * 2.5)),
                Life = (float)((0.6 + (_random.NextDouble() * 0.4)) * _duration),
                ColorIndex = _random.Next(_colors.Length)
            };
        }
    }

    // Three waves of spinning confetti, from the middle, the left and the right of the timer.
    private void StartConfetti(RectangleF area, float sizeScale)
    {
        _gravity = _scale * 2.2f;
        _drag = 1.4f;
        float[] waveOffsets = [0f, -0.35f, 0.35f];
        for (int i = 0; i < _particles.Length; i++)
        {
            int wave = i % 3;
            double angle = (-Math.PI / 2) + ((_random.NextDouble() - 0.5) * Math.PI * 1.5);
            float speed = _scale * (float)(0.8 + (_random.NextDouble() * 1.0));
            _particles[i] = new Particle
            {
                X = _origin.X + (area.Width * waveOffsets[wave]) + (float)((_random.NextDouble() - 0.5) * area.Width * 0.3),
                Y = _origin.Y,
                VelocityX = (float)Math.Cos(angle) * speed,
                VelocityY = (float)Math.Sin(angle) * speed,
                Size = sizeScale * (float)(2 + (_random.NextDouble() * 3)),
                Life = (float)((0.55 + (_random.NextDouble() * 0.35)) * _duration),
                Delay = wave * 0.22f,
                Rotation = (float)(_random.NextDouble() * 360),
                Spin = (float)((_random.NextDouble() - 0.5) * 1440),
                ColorIndex = _random.Next(_colors.Length)
            };
        }
    }

    // Sparkles that drift slowly up and sway, appearing a few at a time.
    private void StartSparkles(RectangleF area, float sizeScale)
    {
        _gravity = -_scale * 0.05f;
        _drag = 0.6f;
        for (int i = 0; i < _particles.Length; i++)
        {
            double angle = (-Math.PI / 2) + ((_random.NextDouble() - 0.5) * Math.PI * 0.9);
            float speed = _scale * (float)(0.1 + (_random.NextDouble() * 0.2));
            float delay = (float)(_random.NextDouble() * 0.4 * _duration);
            _particles[i] = new Particle
            {
                X = _origin.X + (float)((_random.NextDouble() - 0.5) * area.Width * 0.9),
                Y = _origin.Y + (float)((_random.NextDouble() - 0.5) * area.Height * 0.5),
                VelocityX = (float)Math.Cos(angle) * speed,
                VelocityY = (float)Math.Sin(angle) * speed,
                Size = sizeScale * (float)(1 + (_random.NextDouble() * 1.8)),
                Life = (float)((0.45 + (_random.NextDouble() * 0.3)) * _duration),
                Delay = delay,
                Phase = (float)(_random.NextDouble() * Math.PI * 2),
                ColorIndex = _random.Next(_colors.Length)
            };
        }
    }

    // Small squares that shoot out sideways like data packets.
    private void StartDataBits(float sizeScale)
    {
        _gravity = 0f;
        _drag = 2.5f;
        for (int i = 0; i < _particles.Length; i++)
        {
            float direction = i % 2 == 0 ? 1f : -1f;
            float speed = _scale * (float)(0.8 + (_random.NextDouble() * 1.4));
            _particles[i] = new Particle
            {
                X = _origin.X,
                Y = _origin.Y + (float)((_random.NextDouble() - 0.5) * _timerArea.Height * 0.8),
                VelocityX = direction * speed,
                VelocityY = (float)((_random.NextDouble() - 0.5) * _scale * 0.1),
                Size = sizeScale * (float)(1 + (_random.NextDouble() * 1.5)),
                Life = (float)((0.3 + (_random.NextDouble() * 0.3)) * _duration),
                Delay = (float)(_random.NextDouble() * 0.15 * _duration),
                ColorIndex = _random.Next(_colors.Length)
            };
        }
    }

    public override RectangleF GetBounds(EffectAnchors anchors)
    {
        return _bounds;
    }

    public override void Draw(Graphics g, EffectAnchors anchors)
    {
        switch (_style)
        {
            case EffectsStyle.Chaotic:
                DrawChaotic(g);
                break;
            case EffectsStyle.Chill:
                DrawChill(g);
                break;
            case EffectsStyle.Tech:
                DrawTech(g);
                break;
            default:
                DrawRing(g, 0f, 3f);
                DrawDots(g);
                break;
        }
    }

    private void DrawChaotic(Graphics g)
    {
        float age = (float)Age;
        if (age < FlashSeconds)
        {
            using var flash = new SolidBrush(Color.FromArgb(ClampAlpha(140f * (1f - (age / FlashSeconds)) * _intensity), Color.White));
            g.FillRectangle(flash, _window);
        }

        DrawRing(g, 0f, 5f);
        DrawRing(g, 0.15f, 3f);
        DrawRing(g, 0.3f, 2f);

        float alphaScale = 0.5f + (0.5f * _intensity);
        GraphicsState state = g.Save();
        try
        {
            for (int i = 0; i < _particles.Length; i++)
            {
                ref Particle p = ref _particles[i];
                float alpha = ParticleAlpha(ref p, 3f);
                if (alpha <= 0f)
                {
                    continue;
                }

                _brush.Color = Color.FromArgb(ClampAlpha(255f * alpha * alphaScale), _colors[p.ColorIndex]);
                g.ResetTransform();
                g.TranslateTransform(p.X, p.Y);
                g.RotateTransform(p.Rotation);

                // Flips like a falling piece of paper.
                float width = p.Size * 2f * Math.Max(0.15f, Math.Abs((float)Math.Cos(p.Rotation * Math.PI / 90.0)));
                g.FillRectangle(_brush, -width / 2f, -p.Size / 2f, width, p.Size);
            }
        }
        finally
        {
            g.Restore(state);
        }
    }

    private void DrawChill(Graphics g)
    {
        float t = Progress;
        float glow = Bell(EaseInOutSine(t));
        float radius = BloomRadius();
        if (glow > 0f && radius > 1f)
        {
            using var path = new GraphicsPath();
            path.AddEllipse(_origin.X - radius, _origin.Y - radius, radius * 2f, radius * 2f);
            using var brush = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(ClampAlpha(110f * glow * _intensity), _colors[0]),
                SurroundColors = [Color.FromArgb(0, _colors[0])]
            };
            g.FillPath(brush, path);
        }

        float age = (float)Age;
        float sway = SwayAmount();
        for (int i = 0; i < _particles.Length; i++)
        {
            ref Particle p = ref _particles[i];
            float alpha = ParticleAlpha(ref p, 1f);
            if (alpha <= 0f)
            {
                continue;
            }

            // Fades in as gently as it fades out.
            float sinceBorn = (age - p.Delay) / p.Life;
            alpha *= EaseInOutSine(Clamp01(sinceBorn * 4f));
            float x = p.X + (sway * (float)Math.Sin((age * 2.2) + p.Phase));
            Color color = _colors[p.ColorIndex];

            _brush.Color = Color.FromArgb(ClampAlpha(60f * alpha * _intensity), color);
            float halo = p.Size * 3f;
            g.FillEllipse(_brush, x - halo, p.Y - halo, halo * 2f, halo * 2f);

            _brush.Color = Color.FromArgb(ClampAlpha(230f * alpha), color);
            g.FillEllipse(_brush, x - p.Size, p.Y - p.Size, p.Size * 2f, p.Size * 2f);
        }
    }

    private void DrawTech(Graphics g)
    {
        float age = (float)Age;
        float rippleSeconds = (float)Math.Max(0.4, _duration * 0.6);
        DrawGridRipple(g, age / rippleSeconds);
        DrawGridRipple(g, (age - 0.3f) / rippleSeconds);

        for (int i = 0; i < _particles.Length; i++)
        {
            ref Particle p = ref _particles[i];
            float alpha = ParticleAlpha(ref p, 2f);
            if (alpha <= 0f)
            {
                continue;
            }

            _brush.Color = Color.FromArgb(ClampAlpha(255f * alpha * (0.5f + (0.5f * _intensity))), _colors[p.ColorIndex]);
            g.FillRectangle(_brush, p.X - p.Size, p.Y - p.Size, p.Size * 2f, p.Size * 2f);
        }

        DrawRecordLabel(g);
    }

    // A ring that sweeps across the window, lighting up a band of grid lines as it passes.
    private void DrawGridRipple(Graphics g, float progress)
    {
        if (progress <= 0f || progress >= 1f)
        {
            return;
        }

        float maxRadius = FarthestCornerDistance();
        float radius = maxRadius * EaseOutCubic(progress);
        float band = Math.Max(16f, _scale * 0.18f);
        float fade = 1f - progress;

        using (var annulus = new GraphicsPath(FillMode.Alternate))
        {
            float outer = radius + (band / 2f);
            float inner = Math.Max(0f, radius - (band / 2f));
            annulus.AddEllipse(_origin.X - outer, _origin.Y - outer, outer * 2f, outer * 2f);
            if (inner > 0f)
            {
                annulus.AddEllipse(_origin.X - inner, _origin.Y - inner, inner * 2f, inner * 2f);
            }

            Region oldClip = g.Clip;
            g.SetClip(annulus, CombineMode.Intersect);
            using (var grid = new Pen(Color.FromArgb(ClampAlpha(150f * fade * _intensity), _accent), 1f))
            {
                float spacing = Math.Max(8f, _scale * 0.06f);
                float startX = _origin.X - ((float)Math.Ceiling((_origin.X - _window.Left) / spacing) * spacing);
                float startY = _origin.Y - ((float)Math.Ceiling((_origin.Y - _window.Top) / spacing) * spacing);
                for (float x = startX; x <= _window.Right; x += spacing)
                {
                    g.DrawLine(grid, x, _window.Top, x, _window.Bottom);
                }

                for (float y = startY; y <= _window.Bottom; y += spacing)
                {
                    g.DrawLine(grid, _window.Left, y, _window.Right, y);
                }
            }

            g.Clip = oldClip;
            oldClip.Dispose();
        }

        using var ring = new Pen(Color.FromArgb(ClampAlpha(230f * fade * _intensity), Blend(_accent, Color.White, 0.4f)), 2f);
        g.DrawEllipse(ring, _origin.X - radius, _origin.Y - radius, radius * 2f, radius * 2f);
    }

    // "NEW RECORD" types itself out inside lock-on brackets, blinks, and flickers away.
    private void DrawRecordLabel(Graphics g)
    {
        float t = Progress;
        int shown = (int)Math.Ceiling(RecordLabel.Length * Clamp01(t / 0.25f));
        if (shown <= 0)
        {
            return;
        }

        bool blinkOff = t > 0.25f && t < 0.4f && ((int)((t - 0.25f) / 0.035f) % 2 == 1);
        float fadeOut = t < 0.75f ? 1f : 1f - ((t - 0.75f) / 0.25f);
        if (blinkOff || fadeOut <= 0f)
        {
            return;
        }

        float emSize = Math.Max(10f, Math.Min(40f, _timerArea.Height * 0.32f));
        using var path = new GraphicsPath();
        using (var format = new StringFormat(StringFormat.GenericTypographic))
        {
            // Measure the full label so it doesn't shift while typing.
            path.AddString(RecordLabel, TechFontFamily, (int)FontStyle.Bold, emSize, PointF.Empty, format);
            RectangleF full = path.GetBounds();
            path.Reset();
            path.AddString(RecordLabel.Substring(0, shown), TechFontFamily, (int)FontStyle.Bold, emSize, PointF.Empty, format);

            // Above the timer if there's room, otherwise over it.
            float labelY = _timerArea.Top - (full.Height * 1.6f);
            if (labelY < _window.Top + 2f)
            {
                labelY = _origin.Y - (full.Height / 2f);
            }

            using var move = new Matrix();
            move.Translate(_origin.X - (full.Left + (full.Width / 2f)), labelY - full.Top);
            path.Transform(move);
            full.Offset(_origin.X - (full.Left + (full.Width / 2f)), labelY - full.Top);

            RectangleF plate = RectangleF.Inflate(full, emSize * 0.4f, emSize * 0.3f);
            using (var backing = new SolidBrush(Color.FromArgb(ClampAlpha(150f * fadeOut), Color.Black)))
            {
                g.FillRectangle(backing, plate);
            }

            using (var pen = new Pen(Color.FromArgb(ClampAlpha(255f * fadeOut), _accent), 2f))
            {
                DrawCornerBrackets(g, pen, plate, emSize * 0.5f);
            }
        }

        using var fill = new SolidBrush(Color.FromArgb(ClampAlpha(255f * fadeOut), Blend(_accent, Color.White, 0.3f)));
        g.FillPath(fill, path);
    }

    private void DrawRing(Graphics g, float delay, float width)
    {
        float ring = RingRadius(delay);
        if (ring <= 0f)
        {
            return;
        }

        float ringFade = 1f - (((float)Age - delay) / RingSeconds);
        using var pen = new Pen(Color.FromArgb(ClampAlpha(200f * ringFade * _intensity), _colors[0]), width);
        g.DrawEllipse(pen, _origin.X - ring, _origin.Y - ring, ring * 2f, ring * 2f);
    }

    private void DrawDots(Graphics g)
    {
        for (int i = 0; i < _particles.Length; i++)
        {
            ref Particle p = ref _particles[i];
            float alpha = ParticleAlpha(ref p, 2.5f);
            if (alpha <= 0f)
            {
                continue;
            }

            _brush.Color = Color.FromArgb(ClampAlpha(255f * alpha * (0.4f + (0.6f * _intensity))), _colors[p.ColorIndex]);
            g.FillEllipse(_brush, p.X - p.Size, p.Y - p.Size, p.Size * 2f, p.Size * 2f);
        }
    }

    // Each particle fades out over the last part of its own lifetime; 0 before it appears and after it's gone.
    private float ParticleAlpha(ref Particle p, float fadeSharpness)
    {
        float age = (float)Age - p.Delay;
        if (age < 0f)
        {
            return 0f;
        }

        float remaining = (p.Life - age) / p.Life;
        return remaining <= 0f ? 0f : Math.Min(1f, remaining * fadeSharpness);
    }

    protected internal override void OnFinished()
    {
        _brush.Dispose();
    }

    private float RingRadius(float delay)
    {
        float seconds = (float)Age - delay;
        return seconds <= 0f || seconds >= RingSeconds ? 0f : _scale * 0.6f * EaseOutCubic(seconds / RingSeconds);
    }

    private float BloomRadius()
    {
        return _scale * (0.3f + (0.8f * EaseInOutSine(Progress)));
    }

    private float SwayAmount()
    {
        return _style == EffectsStyle.Chill ? _scale * 0.04f : 0f;
    }

    private float FarthestCornerDistance()
    {
        float dx = Math.Max(_origin.X - _window.Left, _window.Right - _origin.X);
        float dy = Math.Max(_origin.Y - _window.Top, _window.Bottom - _origin.Y);
        return (float)Math.Sqrt((dx * dx) + (dy * dy));
    }
}
