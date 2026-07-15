using System.Drawing;
using System.Net;
using System.Net.Sockets;
using Monitor.Protocol;

namespace Monitor.Receiver;

/// <summary>
/// REQUIREMENTS.md §5, §8.2. Listens for senders. One live session per DEVICE_ID; a new connection
/// with the same id always replaces the old one, which is how a rebooted sender recovers from a
/// half-open socket (§13.3). Different ids run side by side — the UI picks which one to show and
/// pauses the rest.
/// </summary>
public sealed class ReceiverServer : IDisposable
{
    private readonly int _port;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    private sealed class Session
    {
        public required TcpClient Client;
        public required NetworkStream Stream;
        public bool Paused;
    }

    /// <summary>Raised off the UI thread. The receiver takes ownership of the bitmap.</summary>
    public event Action<string, int, int, Bitmap>? FrameReceived; // deviceId, monitorIndex, monitorCount

    /// <summary>Raised off the UI thread whenever a sender connects or disconnects.</summary>
    public event Action? SendersChanged;

    public event Action<string>? StatusChanged;

    public ReceiverServer(int port) => _port = port;

    public void Start() => new Thread(AcceptLoop) { IsBackground = true, Name = "accept" }.Start();

    public IReadOnlyList<string> ConnectedSenders
    {
        get { lock (_gate) return _sessions.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(); }
    }

    /// <summary>
    /// §7.4. Told from the UI thread. Pausing is per sender: the one on screen streams, the rest
    /// (and everything while minimised) sit at zero CPU on their machines.
    /// </summary>
    public void SetPaused(string deviceId, bool paused)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(deviceId, out var session) || session.Paused == paused) return;
            session.Paused = paused;

            try
            {
                session.Stream.WriteByte(paused ? Wire.MsgPause : Wire.MsgResume);
            }
            catch
            {
                // The session's read loop will notice and tear it down.
            }
        }
    }

    private void AcceptLoop()
    {
        var listener = new TcpListener(IPAddress.Any, _port);

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"port {_port} unavailable: {ex.Message}");
            return;
        }

        StatusChanged?.Invoke($"waiting on port {_port}");

        using var reg = _stop.Token.Register(listener.Stop);

        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"accept failed: {ex.Message}");
                continue;
            }

            new Thread(() => HandleClient(client)) { IsBackground = true, Name = "session" }.Start();
        }
    }

    private void HandleClient(TcpClient client)
    {
        var peer = client.Client.RemoteEndPoint?.ToString() ?? "?";
        client.NoDelay = true;

        string? deviceId = null;
        Session? session = null;

        try
        {
            var stream = client.GetStream();
            stream.ReadTimeout = (int)Wire.ReadTimeout.TotalMilliseconds;

            var hs = new byte[Wire.HandshakeBytes];
            stream.ReadExactly(hs);

            if (!Wire.TryParseHandshake(hs, out var version, out deviceId))
            {
                StatusChanged?.Invoke($"{peer}: not a sender, dropped");
                return;
            }

            if (version > Wire.Version)
            {
                StatusChanged?.Invoke($"{peer}: sender speaks protocol v{version}, this receiver only knows v{Wire.Version}. Update the receiver.");
                return;
            }

            session = new Session { Client = client, Stream = stream };

            lock (_gate)
            {
                // Same id already live: the newcomer wins (§13.3). Closing the old client makes its
                // read loop throw; its finally sees it is no longer the registered session and
                // leaves ours alone.
                if (_sessions.TryGetValue(deviceId, out var old)) old.Client.Close();
                _sessions[deviceId] = session;
            }

            StatusChanged?.Invoke($"connected: {deviceId} ({peer})");
            SendersChanged?.Invoke();

            ReadMessages(stream, deviceId);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"{deviceId ?? peer} disconnected: {ex.GetType().Name}");
        }
        finally
        {
            client.Close();

            var removed = false;
            if (deviceId is not null && session is not null)
            {
                lock (_gate)
                {
                    if (_sessions.TryGetValue(deviceId, out var current) && ReferenceEquals(current, session))
                        removed = _sessions.Remove(deviceId);
                }
            }

            if (removed) SendersChanged?.Invoke();
        }
    }

    private void ReadMessages(NetworkStream stream, string deviceId)
    {
        var header = new byte[Wire.Frame2HeaderBytes];
        byte[] jpeg = new byte[1 << 20];

        while (!_stop.IsCancellationRequested)
        {
            var type = stream.ReadByte();
            if (type < 0) return;

            if (type == Wire.MsgPing) continue;

            int length, width, height, monitorIndex, monitorCount;
            switch (type)
            {
                case Wire.MsgFrame:
                    // v1 sender: the whole virtual screen as one image. Shown as a single monitor.
                    stream.ReadExactly(header, 0, Wire.FrameHeaderBytes);
                    Wire.ReadFrameHeader(header, out length, out width, out height);
                    monitorIndex = 0;
                    monitorCount = 1;
                    break;

                case Wire.MsgFrame2:
                    stream.ReadExactly(header, 0, Wire.Frame2HeaderBytes);
                    Wire.ReadFrame2Header(header, out length, out width, out height, out monitorIndex, out monitorCount);
                    if (monitorCount < 1 || monitorCount > Wire.MaxMonitors || monitorIndex >= monitorCount)
                        throw new InvalidDataException($"monitor {monitorIndex} of {monitorCount} out of range");
                    break;

                default:
                    throw new InvalidDataException($"unknown message type 0x{type:X2}");
            }

            if (length <= 0 || length > Wire.MaxJpegBytes)
                throw new InvalidDataException($"frame length out of range: {length}");

            if (jpeg.Length < length) jpeg = new byte[length];
            stream.ReadExactly(jpeg, 0, length);

            // Image.FromStream keeps the stream alive for the image's lifetime, so copy into a Bitmap
            // we own and hand that off.
            using var ms = new MemoryStream(jpeg, 0, length, writable: false);
            using var decoded = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);
            var frame = new Bitmap(decoded);

            FrameReceived?.Invoke(deviceId, monitorIndex, monitorCount, frame);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_gate)
        {
            foreach (var s in _sessions.Values) s.Client.Close();
            _sessions.Clear();
        }
        _stop.Dispose();
    }
}
