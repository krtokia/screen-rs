using System.Drawing;
using System.Net;
using System.Net.Sockets;
using Monitor.Protocol;

namespace Monitor.Receiver;

/// <summary>
/// REQUIREMENTS.md §5, §8.2. Listens for the sender. Exactly one connection is live at a time;
/// a new one always wins, which is how a rebooted sender recovers from a half-open socket (§13.3).
/// </summary>
public sealed class ReceiverServer : IDisposable
{
    private readonly int _port;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();

    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _paused;

    /// <summary>Raised off the UI thread. The receiver takes ownership of the bitmap.</summary>
    public event Action<Bitmap, string>? FrameReceived;

    public event Action<string>? StatusChanged;

    public ReceiverServer(int port) => _port = port;

    public void Start() => new Thread(AcceptLoop) { IsBackground = true, Name = "accept" }.Start();

    /// <summary>§7.4. Told from the UI thread whenever the window becomes (in)visible.</summary>
    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_paused == paused || _stream is null) return;
            _paused = paused;

            try
            {
                _stream.WriteByte(paused ? Wire.MsgPause : Wire.MsgResume);
            }
            catch
            {
                // The read loop will notice and tear the session down.
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

            DropCurrent();
            HandleClient(client);
        }
    }

    private void DropCurrent()
    {
        lock (_gate)
        {
            _client?.Close();
            _client = null;
            _stream = null;
        }
    }

    private void HandleClient(TcpClient client)
    {
        var peer = client.Client.RemoteEndPoint?.ToString() ?? "?";
        client.NoDelay = true;

        try
        {
            var stream = client.GetStream();
            stream.ReadTimeout = (int)Wire.ReadTimeout.TotalMilliseconds;

            var hs = new byte[Wire.HandshakeBytes];
            stream.ReadExactly(hs);

            if (!Wire.TryParseHandshake(hs, out var version, out var deviceId))
            {
                StatusChanged?.Invoke($"{peer}: not a sender, dropped");
                return;
            }

            if (version > Wire.Version)
            {
                StatusChanged?.Invoke($"{peer}: sender speaks protocol v{version}, this receiver only knows v{Wire.Version}. Update the receiver.");
                return;
            }

            lock (_gate)
            {
                _client = client;
                _stream = stream;
                _paused = false;
            }

            StatusChanged?.Invoke($"connected: {deviceId} ({peer})");
            ReadMessages(stream, deviceId);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"disconnected: {ex.GetType().Name}");
        }
        finally
        {
            client.Close();
            lock (_gate)
            {
                if (ReferenceEquals(_client, client)) { _client = null; _stream = null; }
            }
            StatusChanged?.Invoke($"waiting on port {_port}");
        }
    }

    private void ReadMessages(NetworkStream stream, string deviceId)
    {
        var header = new byte[Wire.FrameHeaderBytes];
        byte[] jpeg = new byte[1 << 20];

        while (!_stop.IsCancellationRequested)
        {
            var type = stream.ReadByte();
            if (type < 0) return;

            if (type == Wire.MsgPing) continue;

            if (type != Wire.MsgFrame)
                throw new InvalidDataException($"unknown message type 0x{type:X2}");

            stream.ReadExactly(header);
            Wire.ReadFrameHeader(header, out var length, out var width, out var height);

            if (length <= 0 || length > Wire.MaxJpegBytes)
                throw new InvalidDataException($"frame length out of range: {length}");

            if (jpeg.Length < length) jpeg = new byte[length];
            stream.ReadExactly(jpeg, 0, length);

            // Image.FromStream keeps the stream alive for the image's lifetime, so copy into a Bitmap
            // we own and hand that off.
            using var ms = new MemoryStream(jpeg, 0, length, writable: false);
            using var decoded = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);
            var frame = new Bitmap(decoded);

            FrameReceived?.Invoke(frame, $"{deviceId}  {width}x{height}");
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        DropCurrent();
        _stop.Dispose();
    }
}
