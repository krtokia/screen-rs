using System.Diagnostics;
using System.Net.Sockets;
using Monitor.Protocol;

namespace Monitor.Sender;

/// <summary>
/// REQUIREMENTS.md §5, §8.1. Dials the receiver forever. Never throws out, never exits.
/// </summary>
public sealed class SenderLoop
{
    private readonly CancellationToken _stop;
    private readonly AutoResetEvent _kick = new(false);
    private volatile bool _paused;
    private volatile TcpClient? _active;

    public SenderLoop(CancellationToken stop) => _stop = stop;

    /// <summary>
    /// Called from the UI thread after the operator saves new settings: drop the live session (if
    /// any) and retry immediately with the new values, even if the loop is deep in backoff.
    /// </summary>
    public void Reconnect()
    {
        try { _active?.Close(); } catch { }
        _kick.Set();
    }

    /// <summary>Last line of defence: the sender is remote and must never die. Any escape is swallowed.</summary>
    public void Run()
    {
        var backoff = BuildConfig.ReconnectMin;

        while (!_stop.IsCancellationRequested)
        {
            try
            {
                RunSession();
                backoff = BuildConfig.ReconnectMin;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"session ended: {ex.GetType().Name}: {ex.Message}");
            }

            switch (WaitHandle.WaitAny([_stop.WaitHandle, _kick], backoff))
            {
                case 0:
                    return;
                case 1: // settings changed — retry now, and from the shortest interval again
                    backoff = BuildConfig.ReconnectMin;
                    continue;
            }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, BuildConfig.ReconnectMax.Ticks));
        }
    }

    private void RunSession()
    {
        var settings = Settings.Current;

        using var client = new TcpClient { NoDelay = true };
        _active = client;
        try
        {
            client.Connect(settings.ReceiverHost, settings.ReceiverPort);

            using var stream = client.GetStream();
            stream.WriteTimeout = 15_000;

            Log.Info($"connected to {settings.ReceiverHost}:{settings.ReceiverPort} as {settings.DeviceId}");

            stream.Write(Wire.BuildHandshake(settings.DeviceId));

            _paused = false;
            using var sessionEnded = CancellationTokenSource.CreateLinkedTokenSource(_stop);
            var control = Task.Run(() => ReadControl(stream, sessionEnded));

            try
            {
                Pump(stream, sessionEnded.Token);
            }
            finally
            {
                sessionEnded.Cancel();
                client.Close();
                control.Wait(TimeSpan.FromSeconds(2));
            }
        }
        finally
        {
            _active = null;
        }
    }

    private void Pump(NetworkStream stream, CancellationToken sessionEnded)
    {
        using var capturer = new ScreenCapturer();
        var header = new byte[1 + Wire.Frame2HeaderBytes];
        var lastPing = Stopwatch.StartNew();

        while (!sessionEnded.IsCancellationRequested)
        {
            if (_paused)
            {
                // §13.3 — keep proving we are alive so a half-open connection gets noticed.
                if (lastPing.Elapsed >= Wire.PingInterval)
                {
                    stream.WriteByte(Wire.MsgPing);
                    lastPing.Restart();
                }

                if (sessionEnded.WaitHandle.WaitOne(200)) return;
                continue;
            }

            var sw = Stopwatch.StartNew();

            // One FRAME2 per monitor per tick. The receiver decides what to show; the sender
            // stays dumb so it never needs a revisit for a display-side feature.
            var count = capturer.RefreshLayout();
            for (var i = 0; i < count; i++)
            {
                var (buffer, length, width, height) = capturer.CaptureMonitor(i);
                Wire.WriteFrame2Header(header, length, width, height, i, count);

                // §10 — this Write blocks when the link is slow, so the next capture simply happens
                // later. Frames are dropped by never being taken, and latency cannot accumulate.
                stream.Write(header);
                stream.Write(buffer, 0, length);
            }
            lastPing.Restart();

            var remaining = BuildConfig.FrameInterval - sw.Elapsed;
            if (remaining > TimeSpan.Zero && sessionEnded.WaitHandle.WaitOne(remaining)) return;
        }
    }

    /// <summary>
    /// §7.4. PAUSE and RESUME are the only bytes with meaning. Anything else is ignored on purpose so
    /// that a future receiver can add messages without a redeploy of this remote binary.
    /// </summary>
    private void ReadControl(NetworkStream stream, CancellationTokenSource sessionEnded)
    {
        try
        {
            while (!sessionEnded.IsCancellationRequested)
            {
                var b = stream.ReadByte();
                if (b < 0) break;

                switch ((byte)b)
                {
                    case Wire.MsgPause:
                        _paused = true;
                        Log.Info("paused by receiver");
                        break;
                    case Wire.MsgResume:
                        _paused = false;
                        Log.Info("resumed by receiver");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"control channel closed: {ex.GetType().Name}");
        }
        finally
        {
            sessionEnded.Cancel();
        }
    }
}
