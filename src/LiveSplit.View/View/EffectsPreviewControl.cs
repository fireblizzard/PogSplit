using LiveSplit.UI.Effects;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace LiveSplit.View;

/// <summary>
/// A small mock timer in the Effects tab that plays the real effects with the current settings,
/// so they can be tried out without running.
/// </summary>
public class EffectsPreviewControl : Control
{
    private static readonly string[] SplitNames = ["Black Mesa Inbound", "Anomalous Materials", "Unforeseen Consequences", "Office Complex"];
    private static readonly string[] SplitTimes = ["1:12.4", "4:31.0", "8:05.7", "11:42.3"];
    private static readonly string[] Deltas = ["-0.8", "-3.4", "-2.9", "-4.1"];
    private const int GoldRow = 1;
    private const string TimerText = "11:42.31";

    private readonly Options.LayoutSettings settings;
    private readonly EffectsManager effects;
    private readonly System.Windows.Forms.Timer frameTimer;
    private bool lastWasPersonalBest;

    public EffectsPreviewControl(Options.LayoutSettings settings)
    {
        this.settings = settings;
        effects = new EffectsManager(() => this.settings);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        frameTimer = new System.Windows.Forms.Timer { Interval = 15 };
        frameTimer.Tick += (sender, e) =>
        {
            effects.Tick();
            Invalidate();
            if (!effects.HasActiveEffects)
            {
                frameTimer.Stop();
            }
        };
    }

    /// <summary>
    /// Plays the best segment effects on the gold split.
    /// </summary>
    public void PlayBestSegment()
    {
        lastWasPersonalBest = false;
        UpdateAnchors();
        effects.PreviewBestSegment();
        StartAnimating();
    }

    /// <summary>
    /// Plays the new personal best effects on the timer.
    /// </summary>
    public void PlayPersonalBest()
    {
        lastWasPersonalBest = true;
        UpdateAnchors();
        effects.PreviewPersonalBest();
        StartAnimating();
    }

    /// <summary>
    /// Plays whichever effects were played last again, to show a settings change.
    /// </summary>
    public void Replay()
    {
        if (lastWasPersonalBest)
        {
            PlayPersonalBest();
        }
        else
        {
            PlayBestSegment();
        }
    }

    private void StartAnimating()
    {
        // The first tick removes anything still playing from before.
        effects.Tick();
        frameTimer.Start();
        Invalidate();
    }

    private float RowHeight => Math.Max(16f, (ClientSize.Height - 8f) / 6.5f);

    private RectangleF RowRect(int row)
    {
        return new RectangleF(0f, 4f + (row * RowHeight), ClientSize.Width, RowHeight);
    }

    private RectangleF TimerRect()
    {
        float top = 4f + (SplitNames.Length * RowHeight);
        return new RectangleF(0f, top, ClientSize.Width, ClientSize.Height - top);
    }

    private void UpdateAnchors()
    {
        for (int i = 0; i < SplitNames.Length; i++)
        {
            effects.Anchors.Set(EffectAnchors.SplitRow(i), RowRect(i));
        }

        effects.Anchors.Set(EffectAnchors.Timer, TimerRect());
        effects.Anchors.Set(EffectAnchors.Window, new RectangleF(0f, 0f, ClientSize.Width, ClientSize.Height));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Color.FromArgb(20, 20, 26));

        UpdateAnchors();
        PointF shake = effects.GetShakeOffset();
        g.TranslateTransform(shake.X, shake.Y);
        DrawMockLayout(g);
        g.ResetTransform();

        effects.Draw(g);

        if (!settings.EffectsEnabled)
        {
            using var brush = new SolidBrush(Color.FromArgb(200, Color.White));
            using var font = new Font(Font, FontStyle.Italic);
            g.DrawString("Effects are turned off", font, brush, 6f, ClientSize.Height - font.Height - 4f);
        }
    }

    private void DrawMockLayout(Graphics g)
    {
        float rowHeight = RowHeight;
        Color textColor = Opaque(settings.TextColor, Color.White);
        using var nameFont = new Font(settings.TextFont?.FontFamily ?? Font.FontFamily, rowHeight * 0.45f, GraphicsUnit.Pixel);
        using var timeFont = new Font(settings.TimesFont?.FontFamily ?? Font.FontFamily, rowHeight * 0.45f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(textColor);
        using var aheadBrush = new SolidBrush(Opaque(settings.AheadGainingTimeColor, Color.LimeGreen));
        using var goldBrush = new SolidBrush(Opaque(settings.BestSegmentColor, Color.Gold));
        using var separator = new Pen(Color.FromArgb(40, textColor));
        using var centerLeft = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var centerRight = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };

        float timeWidth = rowHeight * 2.6f;
        float deltaWidth = rowHeight * 2f;
        for (int i = 0; i < SplitNames.Length; i++)
        {
            RectangleF row = RowRect(i);
            var nameRect = RectangleF.FromLTRB(row.Left + 6f, row.Top, row.Right - timeWidth - deltaWidth, row.Bottom);
            var deltaRect = RectangleF.FromLTRB(row.Right - timeWidth - deltaWidth, row.Top, row.Right - timeWidth, row.Bottom);
            var timeRect = RectangleF.FromLTRB(row.Right - timeWidth, row.Top, row.Right - 6f, row.Bottom);
            g.DrawString(SplitNames[i], nameFont, textBrush, nameRect, centerLeft);
            g.DrawString(Deltas[i], timeFont, i == GoldRow ? goldBrush : aheadBrush, deltaRect, centerRight);
            g.DrawString(SplitTimes[i], timeFont, textBrush, timeRect, centerRight);
            g.DrawLine(separator, row.Left, row.Bottom, row.Right, row.Bottom);
        }

        RectangleF timer = TimerRect();
        using var timerFont = new Font(settings.TimerFont?.FontFamily ?? Font.FontFamily, Math.Max(10f, timer.Height * 0.75f), FontStyle.Bold, GraphicsUnit.Pixel);
        g.DrawString(TimerText, timerFont, aheadBrush, RectangleF.FromLTRB(timer.Left, timer.Top, timer.Right - 6f, timer.Bottom), centerRight);
    }

    private static Color Opaque(Color color, Color fallback)
    {
        return color.A == 0 ? fallback : Color.FromArgb(255, color);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            frameTimer.Stop();
            frameTimer.Dispose();
            effects.StopAll();
            effects.Tick();
            effects.Dispose();
        }

        base.Dispose(disposing);
    }
}
