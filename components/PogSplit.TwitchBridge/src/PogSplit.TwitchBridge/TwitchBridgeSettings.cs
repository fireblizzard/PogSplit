using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PogSplit.TwitchBridge;

/// <summary>
/// Settings page: connect PogSplit to a Twitch channel by typing a short code into the extension's config page.
/// </summary>
public class TwitchBridgeSettings : UserControl
{
    private const string TwitchExtensionsUrl = "https://dashboard.twitch.tv/extensions";

    private readonly TwitchBridgeComponent component;
    private readonly TextBox ebsUrl;
    private readonly CheckBox keepRunning;
    private readonly CheckBox shareHistory;
    private readonly Label status;
    private readonly Label code;
    private readonly Label hint;
    private readonly Button connect;
    private readonly Button disconnect;

    private CancellationTokenSource pairing;

    public TwitchBridgeSettings(TwitchBridgeComponent component)
    {
        this.component = component;

        Size = new Size(476, 290);
        Padding = new Padding(7);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 7,
            AutoSize = true,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        status = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
        code = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = new Font(FontFamily.GenericMonospace, 20, FontStyle.Bold),
            Visible = false,
        };
        hint = new Label { AutoSize = true, MaximumSize = new Size(340, 0), Anchor = AnchorStyles.Left };
        connect = new Button { Text = "Connect to Twitch", AutoSize = true };
        disconnect = new Button { Text = "Disconnect", AutoSize = true };
        ebsUrl = new TextBox { Dock = DockStyle.Fill };
        keepRunning = new CheckBox
        {
            Text = "Keep the overlay timer running while game time is paused (for auto splitters like BunnySplit)",
            AutoSize = true,
            MaximumSize = new Size(340, 0),
            Anchor = AnchorStyles.Left,
        };
        keepRunning.CheckedChanged += (s, e) => component.SetKeepTimerRunning(keepRunning.Checked);
        shareHistory = new CheckBox
        {
            Text = "Share run history with viewers (past attempts, finished runs and golds)",
            AutoSize = true,
            MaximumSize = new Size(340, 0),
            Anchor = AnchorStyles.Left,
        };
        shareHistory.CheckedChanged += (s, e) => component.SetShareHistory(shareHistory.Checked);

        connect.Click += Connect_Click;
        disconnect.Click += Disconnect_Click;
        ebsUrl.Leave += (s, e) => component.SetEbsUrl(ebsUrl.Text);
        hint.Click += (s, e) => Process.Start(TwitchExtensionsUrl);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(connect);
        buttons.Controls.Add(disconnect);

        table.Controls.Add(new Label { Text = "Status:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        table.Controls.Add(status, 1, 0);
        table.Controls.Add(buttons, 1, 1);
        table.Controls.Add(code, 1, 2);
        table.Controls.Add(hint, 1, 3);
        table.Controls.Add(new Label { Text = "Backend URL:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
        table.Controls.Add(ebsUrl, 1, 4);
        table.Controls.Add(keepRunning, 1, 5);
        table.Controls.Add(shareHistory, 1, 6);
        Controls.Add(table);

        component.StatusChanged += _ => RunOnUi(Refresh);
        Refresh();
    }

    public override void Refresh()
    {
        ebsUrl.Text = component.EbsUrl;
        keepRunning.Checked = component.KeepTimerRunning;
        shareHistory.Checked = component.ShareHistory;
        bool paired = component.Pairing != null;
        connect.Visible = !paired;
        disconnect.Visible = paired;

        if (pairing != null)
        {
            return;
        }

        code.Visible = false;
        hint.Text = "";
        status.Text = component.Status switch
        {
            BridgeStatus.Connected => $"Connected to {ChannelName()}",
            BridgeStatus.Retrying => $"Connected to {ChannelName()}, backend unreachable, retrying",
            BridgeStatus.Unauthorized => "This connection was revoked. Disconnect and connect again.",
            _ when paired => $"Connected to {ChannelName()}, waiting for the timer",
            _ => "Not connected",
        };
        base.Refresh();
    }

    private string ChannelName()
    {
        return component.Pairing?.ChannelName ?? component.Pairing?.ChannelId ?? "Twitch";
    }

    private async void Connect_Click(object sender, EventArgs e)
    {
        component.SetEbsUrl(ebsUrl.Text);
        pairing = new CancellationTokenSource();
        CancellationToken cancel = pairing.Token;
        connect.Enabled = false;
        status.Text = "Getting a code…";

        try
        {
            using EbsClient client = component.CreateClient(TimeSpan.FromSeconds(10));
            DeviceCode device = await Task.Run(() => client.StartPairingAsync(cancel), cancel);

            code.Text = device.UserCode;
            code.Visible = true;
            status.Text = "Waiting for you to enter the code";
            hint.Text = "Open the PogSplit extension's configuration on Twitch (Creator Dashboard, Extensions) and enter this code.";

            DateTime expires = DateTime.UtcNow.AddSeconds(device.ExpiresInSeconds);
            Pairing result = null;
            while (result == null)
            {
                if (DateTime.UtcNow > expires)
                {
                    throw new PairingFailedException("The code expired. Try connecting again.");
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, device.IntervalSeconds)), cancel);
                result = await Task.Run(() => client.PollPairingAsync(device.Code, cancel), cancel);
            }

            FinishPairing();
            component.SetPairing(result);
        }
        catch (OperationCanceledException)
        {
            FinishPairing();
        }
        catch (Exception ex)
        {
            FinishPairing();
            status.Text = ex is PairingFailedException ? ex.Message : "Could not reach the backend. Check the URL and try again.";
            return;
        }

        Refresh();
    }

    private async void Disconnect_Click(object sender, EventArgs e)
    {
        Pairing old = component.Pairing;
        component.SetPairing(null);
        Refresh();

        if (old != null)
        {
            using EbsClient client = component.CreateClient(TimeSpan.FromSeconds(5));
            await Task.Run(() => client.RevokeAsync(old.Token, CancellationToken.None));
        }
    }

    private void FinishPairing()
    {
        pairing?.Dispose();
        pairing = null;
        connect.Enabled = true;
        code.Visible = false;
        hint.Text = "";
    }

    private void RunOnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            pairing?.Cancel();
        }

        base.Dispose(disposing);
    }
}
