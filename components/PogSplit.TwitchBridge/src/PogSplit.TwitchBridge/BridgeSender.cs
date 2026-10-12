using System;
using System.Threading;

namespace PogSplit.TwitchBridge;

public enum BridgeStatus
{
    NotConnected,
    Connected,
    Retrying,
    Unauthorized,
}

/// <summary>
/// Sends messages to the EBS on a background thread so the timer never waits on the network.
/// Only the newest message is kept: unsent messages are merged, never queued.
/// </summary>
public class BridgeSender : IDisposable
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly EbsClient client;
    private readonly string token;
    private readonly object sync = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly CancellationTokenSource stop = new();
    private readonly Thread thread;

    private BridgeMessage pending;
    private long nextSeq = 1;
    private volatile BridgeStatus status = BridgeStatus.Connected;

    public event Action<BridgeStatus> StatusChanged;

    public BridgeStatus Status => status;

    public BridgeSender(EbsClient client, string token)
    {
        this.client = client;
        this.token = token;
        thread = new Thread(Run) { IsBackground = true, Name = "PogSplit Twitch bridge" };
        thread.Start();
    }

    public void Offer(BridgeMessage message)
    {
        lock (sync)
        {
            message.Seq = nextSeq++;
            pending = BridgeMessage.Merge(pending, message);
        }

        wake.Set();
    }

    private void Run()
    {
        TimeSpan backoff = TimeSpan.FromSeconds(1);
        DateTime lastSend = DateTime.MinValue;

        while (!stop.IsCancellationRequested)
        {
            wake.WaitOne(TimeSpan.FromSeconds(1));
            if (stop.IsCancellationRequested || status == BridgeStatus.Unauthorized)
            {
                continue;
            }

            TimeSpan sinceLast = DateTime.UtcNow - lastSend;
            if (sinceLast < MinInterval)
            {
                Sleep(MinInterval - sinceLast);
            }

            BridgeMessage message;
            lock (sync)
            {
                message = pending;
                pending = null;
            }

            if (message == null)
            {
                continue;
            }

            lastSend = DateTime.UtcNow;
            SendResult result = client.PutStateAsync(token, message, stop.Token).GetAwaiter().GetResult();
            switch (result)
            {
                case SendResult.Ok:
                    backoff = TimeSpan.FromSeconds(1);
                    SetStatus(BridgeStatus.Connected);
                    break;

                case SendResult.Unauthorized:
                    SetStatus(BridgeStatus.Unauthorized);
                    break;

                default:
                    lock (sync)
                    {
                        // Keep the failed message underneath anything newer so a snapshot is not lost.
                        pending = BridgeMessage.Merge(message, pending);
                    }

                    SetStatus(BridgeStatus.Retrying);
                    Sleep(backoff);
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                    wake.Set();
                    break;
            }
        }
    }

    private void Sleep(TimeSpan time)
    {
        stop.Token.WaitHandle.WaitOne(time);
    }

    private void SetStatus(BridgeStatus value)
    {
        if (status != value)
        {
            status = value;
            StatusChanged?.Invoke(value);
        }
    }

    /// <summary>
    /// Stops the background thread and tells viewers PogSplit went offline, waiting at most a moment.
    /// </summary>
    public void Dispose()
    {
        stop.Cancel();
        wake.Set();
        thread.Join(TimeSpan.FromSeconds(1));

        if (status != BridgeStatus.Unauthorized)
        {
            BridgeMessage offline = MessageBuilder.BuildOffline();
            offline.Seq = nextSeq++;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            try
            {
                client.PutStateAsync(token, offline, timeout.Token).Wait(TimeSpan.FromMilliseconds(1500));
            }
            catch (AggregateException)
            {
            }
        }

        wake.Dispose();
        stop.Dispose();
    }
}
