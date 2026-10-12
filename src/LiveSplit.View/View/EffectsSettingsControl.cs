using LiveSplit.Options;
using LiveSplit.UI;
using LiveSplit.UI.Effects;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace LiveSplit.View;

/// <summary>
/// The Effects tab of the Layout Settings dialog.
/// </summary>
public class EffectsSettingsControl : UserControl
{
    private const float RowHeight = 29f;
    private const float PreviewHeight = 170f;

    private readonly TableLayoutPanel table;
    private readonly CheckBox chkEffectsEnabled;
    private readonly CheckBox chkGoldSplitFlash;
    private readonly CheckBox chkGoldTimeSave;
    private readonly CheckBox chkPbBurst;
    private readonly CheckBox chkGoldStreak;
    private readonly CheckBox chkScreenShake;
    private readonly List<Control> effectControls = [];
    private readonly List<Control> goldFlashControls = [];
    private readonly List<Control> timeSaveControls = [];
    private readonly List<Control> pbBurstControls = [];
    private readonly List<Control> shakeControls = [];
    private readonly List<Control> accentControls = [];

    // Re-reads every control's value from the settings, after a style fills them in.
    private readonly List<Action> refreshers = [];

    private readonly EffectsPreviewControl preview;
    private readonly EffectsManager timerEffects;

    // Waits for a burst of changes, like dragging a slider, to settle before replaying the preview.
    private readonly System.Windows.Forms.Timer replayDelay = new() { Interval = 350 };
    private float totalRowHeight;

    /// <param name="timerEffects">The timer window's effects, for playing previews on the timer itself, or null.</param>
    public EffectsSettingsControl(Options.LayoutSettings settings, EffectsManager timerEffects = null)
    {
        Settings = settings;
        this.timerEffects = timerEffects;

        table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            Padding = new Padding(7)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        preview = AddPreview();
        AddPreviewButtons();

        chkEffectsEnabled = AddCheckBox("Enable Effects", 0);
        chkEffectsEnabled.Checked = Settings.EffectsEnabled;
        chkEffectsEnabled.CheckedChanged += (sender, e) =>
        {
            Settings.EffectsEnabled = chkEffectsEnabled.Checked;
            UpdateEnabledState();
        };

        AddStyleRow();
        AddAccentColorRow();
        AddIntensityRow();

        AddHeader("Best Segment");
        chkGoldSplitFlash = AddCheckBox("Flash Split on Best Segment", 20);
        chkGoldSplitFlash.Checked = Settings.GoldSplitFlash;
        chkGoldSplitFlash.CheckedChanged += (sender, e) =>
        {
            Settings.GoldSplitFlash = chkGoldSplitFlash.Checked;
            UpdateEnabledState();
        };
        AddNumber(goldFlashControls, "Flash Duration:", "seconds", 0.1m, 5m, 2, 0.05m,
            () => Settings.GoldFlashDuration, value => Settings.GoldFlashDuration = value);

        chkGoldTimeSave = AddCheckBox("Show Time Saved on Best Segment", 20);
        chkGoldTimeSave.Checked = Settings.GoldTimeSave;
        chkGoldTimeSave.CheckedChanged += (sender, e) =>
        {
            Settings.GoldTimeSave = chkGoldTimeSave.Checked;
            UpdateEnabledState();
        };
        AddNumber(timeSaveControls, "Duration:", "seconds", 0.1m, 5m, 2, 0.05m,
            () => Settings.TimeSaveDuration, value => Settings.TimeSaveDuration = value);
        AddNumber(timeSaveControls, "Fade Out Starts At:", "% of duration", 0m, 99m, 0, 5m,
            () => Settings.TimeSaveFadeStart * 100f, value => Settings.TimeSaveFadeStart = value / 100f);
        AddNumber(timeSaveControls, "Rotation:", "degrees", -360m, 360m, 0, 5m,
            () => Settings.TimeSaveRotation, value => Settings.TimeSaveRotation = value);
        AddRotationDirectionRow(timeSaveControls);
        AddNumber(timeSaveControls, "Drift Down:", "% of split height", -300m, 300m, 0, 10m,
            () => Settings.TimeSaveDrift * 100f, value => Settings.TimeSaveDrift = value / 100f);
        AddNumber(timeSaveControls, "Text Size:", "% of split height", 10m, 300m, 0, 5m,
            () => Settings.TimeSaveTextSize * 100f, value => Settings.TimeSaveTextSize = value / 100f);
        AddNumber(timeSaveControls, "Pop Size:", "% of text size", 10m, 500m, 0, 5m,
            () => Settings.TimeSavePopScale * 100f, value => Settings.TimeSavePopScale = value / 100f);

        chkGoldStreak = AddCheckBox("Show Streak of Best Segments in a Row", 20);
        chkGoldStreak.Checked = Settings.GoldStreak;
        chkGoldStreak.CheckedChanged += (sender, e) => Settings.GoldStreak = chkGoldStreak.Checked;
        refreshers.Add(() => chkGoldStreak.Checked = Settings.GoldStreak);

        AddHeader("Screen Shake");
        chkScreenShake = AddCheckBox("Shake on Best Segment and Personal Best", 20);
        chkScreenShake.Checked = Settings.ScreenShake;
        chkScreenShake.CheckedChanged += (sender, e) =>
        {
            Settings.ScreenShake = chkScreenShake.Checked;
            UpdateEnabledState();
        };
        refreshers.Add(() => chkScreenShake.Checked = Settings.ScreenShake);
        AddNumber(shakeControls, "Strength:", "pixels", 1m, 40m, 0, 1m,
            () => Settings.ShakeStrength, value => Settings.ShakeStrength = value);

        AddHeader("Personal Best");
        chkPbBurst = AddCheckBox("Particle Burst on New Personal Best", 20);
        chkPbBurst.Checked = Settings.PbBurst;
        chkPbBurst.CheckedChanged += (sender, e) =>
        {
            Settings.PbBurst = chkPbBurst.Checked;
            UpdateEnabledState();
        };
        AddNumber(pbBurstControls, "Burst Duration:", "seconds", 0.5m, 10m, 1, 0.1m,
            () => Settings.PbBurstDuration, value => Settings.PbBurstDuration = value);

        effectControls.AddRange([chkGoldSplitFlash, chkGoldTimeSave, chkGoldStreak, chkScreenShake, chkPbBurst]);

        Controls.Add(table);
        // This tab is taller than the dialog, so leave room for the vertical scrollbar instead of scrolling sideways.
        Size = new Size(476 - SystemInformation.VerticalScrollBarWidth, (int)(totalRowHeight + 20));

        UpdateEnabledState();

        replayDelay.Tick += (sender, e) =>
        {
            replayDelay.Stop();
            preview.Replay();
        };
        WatchForChanges(table);
        HandleCreated += (sender, e) => BeginInvoke(new Action(preview.PlayBestSegment));
    }

    public Options.LayoutSettings Settings { get; }

    private int NextRow(float height = RowHeight)
    {
        int row = table.RowCount;
        table.RowCount = row + 1;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        totalRowHeight += height;
        return row;
    }

    private EffectsPreviewControl AddPreview()
    {
        int row = NextRow(PreviewHeight);
        var control = new EffectsPreviewControl(Settings)
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(3, 3, 3, 6)
        };
        table.Controls.Add(control, 0, row);
        table.SetColumnSpan(control, 3);
        return control;
    }

    private void AddPreviewButtons()
    {
        int row = NextRow(RowHeight + 4f);
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            WrapContents = false
        };
        panel.Controls.Add(CreateButton("Play Best Segment", (sender, e) => preview.PlayBestSegment()));
        panel.Controls.Add(CreateButton("Play Personal Best", (sender, e) => preview.PlayPersonalBest()));

        Button onTimer = CreateButton("Play on Timer...", (sender, e) => ShowTimerMenu((Control)sender));
        onTimer.Enabled = timerEffects != null;
        panel.Controls.Add(onTimer);

        table.Controls.Add(panel, 0, row);
        table.SetColumnSpan(panel, 3);
    }

    private static Button CreateButton(string text, EventHandler click)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(4, 0, 4, 0)
        };
        button.Click += click;
        return button;
    }

    // Plays the effects on the real timer window, behind this dialog.
    private void ShowTimerMenu(Control button)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Best Segment", null, (sender, e) =>
        {
            if (!Settings.EffectsEnabled)
            {
                MessageBox.Show(this, "Effects are turned off.", "Play on Timer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (!timerEffects.PreviewBestSegment())
            {
                MessageBox.Show(this, "No splits are showing on the timer, so the best segment effects have nowhere to play. Add a Splits component to your layout to see them.", "Play on Timer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        });
        menu.Items.Add("Personal Best", null, (sender, e) => timerEffects.PreviewPersonalBest());
        menu.Closed += (sender, e) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(button, new Point(0, button.Height));
    }

    // Replays the preview shortly after any setting changes.
    private void WatchForChanges(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            switch (control)
            {
                case CheckBox checkBox:
                    checkBox.CheckedChanged += (sender, e) => QueueReplay();
                    break;
                case NumericUpDown number:
                    number.ValueChanged += (sender, e) => QueueReplay();
                    break;
                case TrackBar trackBar:
                    trackBar.ValueChanged += (sender, e) => QueueReplay();
                    break;
                case ComboBox comboBox:
                    comboBox.SelectedIndexChanged += (sender, e) => QueueReplay();
                    break;
                case Button button when accentControls.Contains(button):
                    button.BackColorChanged += (sender, e) => QueueReplay();
                    break;
                case FlowLayoutPanel:
                case EffectsPreviewControl:
                    break;
                default:
                    WatchForChanges(control);
                    break;
            }
        }
    }

    private void QueueReplay()
    {
        replayDelay.Stop();
        replayDelay.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            replayDelay.Dispose();
        }

        base.Dispose(disposing);
    }

    private CheckBox AddCheckBox(string text, int indent)
    {
        int row = NextRow();
        var checkBox = new CheckBox
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(indent + 3, 3, 3, 3)
        };
        table.Controls.Add(checkBox, 0, row);
        table.SetColumnSpan(checkBox, 3);
        return checkBox;
    }

    private void AddHeader(string text)
    {
        int row = NextRow();
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
            Font = new Font(Font, FontStyle.Bold)
        };
        table.Controls.Add(label, 0, row);
        table.SetColumnSpan(label, 3);
        effectControls.Add(label);
    }

    private void AddStyleRow()
    {
        int row = NextRow();
        var label = new Label
        {
            Text = "Style:",
            AutoSize = true,
            Anchor = AnchorStyles.Left
        };
        var comboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Left | AnchorStyles.Right
        };
        comboBox.Items.AddRange([EffectsStyle.Classic, EffectsStyle.Chaotic, EffectsStyle.Chill, EffectsStyle.Tech]);
        comboBox.SelectedItem = Settings.EffectsStyle;
        comboBox.SelectedIndexChanged += (sender, e) =>
        {
            if (comboBox.SelectedItem is EffectsStyle style && style != Settings.EffectsStyle)
            {
                EffectsPresets.Apply(Settings, style);
                RefreshValues();
            }
        };
        var hint = new Label
        {
            Text = "resets the values below",
            AutoSize = true,
            Anchor = AnchorStyles.Left
        };
        table.Controls.Add(label, 0, row);
        table.Controls.Add(comboBox, 1, row);
        table.Controls.Add(hint, 2, row);
        effectControls.AddRange([label, comboBox, hint]);
    }

    private void AddAccentColorRow()
    {
        int row = NextRow();
        var label = new Label
        {
            Text = "Tech Accent Color:",
            AutoSize = true,
            Anchor = AnchorStyles.Left
        };
        var button = new Button
        {
            FlatStyle = FlatStyle.Popup,
            Size = new Size(23, 23),
            Anchor = AnchorStyles.Left,
            BackColor = Settings.EffectsAccentColor
        };
        button.Click += (sender, e) => SettingsHelper.ColorButtonClick(button, this);
        button.BackColorChanged += (sender, e) => Settings.EffectsAccentColor = button.BackColor;
        table.Controls.Add(label, 0, row);
        table.Controls.Add(button, 1, row);
        accentControls.AddRange([label, button]);
    }

    private void AddIntensityRow()
    {
        int row = NextRow();
        var label = new Label
        {
            Text = "Intensity:",
            AutoSize = true,
            Anchor = AnchorStyles.Left
        };
        var trackBar = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            TickStyle = TickStyle.None,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Value = Clamp((int)Math.Round(Settings.EffectsIntensity * 100f), 0, 100)
        };
        trackBar.ValueChanged += (sender, e) => Settings.EffectsIntensity = trackBar.Value / 100f;
        table.Controls.Add(label, 0, row);
        table.Controls.Add(trackBar, 1, row);
        table.SetColumnSpan(trackBar, 2);
        effectControls.Add(label);
        effectControls.Add(trackBar);
    }

    private void AddNumber(List<Control> group, string text, string unit, decimal min, decimal max, int decimals, decimal increment, Func<float> get, Action<float> set)
    {
        int row = NextRow();
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(40, 3, 3, 3)
        };
        var number = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            DecimalPlaces = decimals,
            Increment = increment,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Value = Math.Max(min, Math.Min(max, Math.Round((decimal)get(), decimals)))
        };
        number.ValueChanged += (sender, e) => set((float)number.Value);
        refreshers.Add(() => number.Value = Math.Max(min, Math.Min(max, Math.Round((decimal)get(), decimals))));
        var unitLabel = new Label
        {
            Text = unit,
            AutoSize = true,
            Anchor = AnchorStyles.Left
        };
        table.Controls.Add(label, 0, row);
        table.Controls.Add(number, 1, row);
        table.Controls.Add(unitLabel, 2, row);
        group.AddRange([label, number, unitLabel]);
    }

    private void AddRotationDirectionRow(List<Control> group)
    {
        int row = NextRow();
        var label = new Label
        {
            Text = "Rotation Direction:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(40, 3, 3, 3)
        };
        var comboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Left | AnchorStyles.Right
        };
        comboBox.Items.AddRange([RotationDirection.Clockwise, RotationDirection.Anticlockwise, RotationDirection.Random]);
        comboBox.SelectedItem = Settings.TimeSaveRotationDirection;
        comboBox.SelectedIndexChanged += (sender, e) =>
        {
            if (comboBox.SelectedItem is RotationDirection direction)
            {
                Settings.TimeSaveRotationDirection = direction;
            }
        };
        table.Controls.Add(label, 0, row);
        table.Controls.Add(comboBox, 1, row);
        group.AddRange([label, comboBox]);
    }

    private static int Clamp(int value, int min, int max)
    {
        return Math.Max(min, Math.Min(max, value));
    }

    private void RefreshValues()
    {
        foreach (Action refresh in refreshers)
        {
            refresh();
        }

        UpdateEnabledState();
    }

    private void UpdateEnabledState()
    {
        bool enabled = chkEffectsEnabled.Checked;
        SetEnabled(accentControls, enabled && Settings.EffectsStyle == EffectsStyle.Tech);
        SetEnabled(shakeControls, enabled && chkScreenShake.Checked);
        SetEnabled(effectControls, enabled);
        SetEnabled(goldFlashControls, enabled && chkGoldSplitFlash.Checked);
        SetEnabled(timeSaveControls, enabled && chkGoldTimeSave.Checked);
        SetEnabled(pbBurstControls, enabled && chkPbBurst.Checked);
    }

    private static void SetEnabled(List<Control> controls, bool enabled)
    {
        foreach (Control control in controls)
        {
            control.Enabled = enabled;
        }
    }
}
