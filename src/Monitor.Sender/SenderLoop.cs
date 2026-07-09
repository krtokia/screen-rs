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
    private volatile bool _paused;

    public SenderLoop(CancellationToken stop) => _stop = stop;

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

            if (_stop.WaitHandle.WaitOne(backoff)) return;
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, BuildConfig.ReconnectMax.Ticks));
        }
    }

    private void RunSession()
    {
        using var client = new TcpClient { NoDelay = true };
        client.Connect(BuildConfig.ReceiverHost, BuildConfig.ReceiverPort);

        using var stream = client.GetStream();
        stream.WriteTimeout = 15_000;

        Log.Info($"connected to {BuildConfig.ReceiverHost}:{BuildConfig.ReceiverPort}");

        stream.Write(Wire.BuildHandshake(BuildConfig.DeviceId));

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

    private void Pump(NetworkStream stream, CancellationToken sessionEnded)
    {
        using var capturer = new ScreenCapturer();
        var header = new byte[1 + Wire.FrameHeaderBytes];
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

            var (buffer, length, width, height) = capturer.CaptureJpeg();
            Wire.WriteFrameHeader(header, length, width, height);

            // §10 — this Write blocks when the link is slow, so the next capture simply happens
            // later. Frames are dropped by never being taken, and latency cannot accumulate.
            stream.Write(header);
            stream.Write(buffer, 0, length);
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
