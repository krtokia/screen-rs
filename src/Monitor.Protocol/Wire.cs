using System.Buffers.Binary;
using System.Text;

namespace Monitor.Protocol;

/// <summary>
/// REQUIREMENTS.md §7. All integers are little-endian.
/// </summary>
public static class Wire
{
    /// <summary>"OSM\x01" — 4 bytes.</summary>
    public static ReadOnlySpan<byte> Magic => [0x4F, 0x53, 0x4D, 0x01];

    /// <summary>v2 added FRAME2 (per-monitor frames). v1 senders in the field stay valid forever.</summary>
    public const byte Version = 0x02;

    public const int MagicBytes = 4;
    public const int DeviceIdBytes = 16;
    public const int HandshakeBytes = MagicBytes + 1 + DeviceIdBytes;

    // Sender -> Receiver
    public const byte MsgFrame = 0x01;
    public const byte MsgPing = 0x02;
    public const byte MsgFrame2 = 0x03;

    // Receiver -> Sender
    public const byte MsgPause = 0x10;
    public const byte MsgResume = 0x11;

    /// <summary>v1 FRAME bytes after the type byte: jpegLength, width, height.</summary>
    public const int FrameHeaderBytes = 12;

    /// <summary>FRAME2 bytes after the type byte: jpegLength, width, height, monitorIndex, monitorCount.</summary>
    public const int Frame2HeaderBytes = 14;

    /// <summary>Sanity bound for FRAME2's monitorCount; a machine will never drive this many displays.</summary>
    public const int MaxMonitors = 16;

    public const int DefaultPort = 45871;

    /// <summary>Sender emits PING at this interval while paused, so the receiver can detect a dead peer.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);

    /// <summary>Receiver drops a connection that has produced no bytes for this long. §13.3</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Sanity bound so a corrupt length prefix cannot make the receiver allocate wildly.</summary>
    public const int MaxJpegBytes = 32 * 1024 * 1024;

    public static byte[] BuildHandshake(string deviceId)
    {
        var buf = new byte[HandshakeBytes];
        Magic.CopyTo(buf);
        buf[MagicBytes] = Version;

        var id = Encoding.UTF8.GetBytes(deviceId);
        if (id.Length > DeviceIdBytes)
            throw new ArgumentException($"deviceId exceeds {DeviceIdBytes} UTF-8 bytes: {deviceId}", nameof(deviceId));
        id.CopyTo(buf, MagicBytes + 1);

        return buf;
    }

    public static bool TryParseHandshake(ReadOnlySpan<byte> buf, out byte version, out string deviceId)
    {
        version = 0;
        deviceId = "";

        if (buf.Length != HandshakeBytes) return false;
        if (!buf[..MagicBytes].SequenceEqual(Magic)) return false;

        version = buf[MagicBytes];

        var id = buf.Slice(MagicBytes + 1, DeviceIdBytes);
        var end = id.IndexOf((byte)0);
        deviceId = Encoding.UTF8.GetString(end < 0 ? id : id[..end]);
        return true;
    }

    public static void WriteFrameHeader(Span<byte> dst, int jpegLength, int width, int height)
    {
        dst[0] = MsgFrame;
        BinaryPrimitives.WriteInt32LittleEndian(dst[1..], jpegLength);
        BinaryPrimitives.WriteInt32LittleEndian(dst[5..], width);
        BinaryPrimitives.WriteInt32LittleEndian(dst[9..], height);
    }

    public static void ReadFrameHeader(ReadOnlySpan<byte> src, out int jpegLength, out int width, out int height)
    {
        jpegLength = BinaryPrimitives.ReadInt32LittleEndian(src);
        width = BinaryPrimitives.ReadInt32LittleEndian(src[4..]);
        height = BinaryPrimitives.ReadInt32LittleEndian(src[8..]);
    }

    /// <summary>
    /// FRAME2 carries one monitor per message. monitorIndex is 0-based, ordered left-to-right in the
    /// sender's physical layout; monitorCount rides on every frame so a runtime display change needs
    /// no reconnect, mirroring how v1 carries width/height per frame.
    /// </summary>
    public static void WriteFrame2Header(Span<byte> dst, int jpegLength, int width, int height, int monitorIndex, int monitorCount)
    {
        dst[0] = MsgFrame2;
        BinaryPrimitives.WriteInt32LittleEndian(dst[1..], jpegLength);
        BinaryPrimitives.WriteInt32LittleEndian(dst[5..], width);
        BinaryPrimitives.WriteInt32LittleEndian(dst[9..], height);
        dst[13] = checked((byte)monitorIndex);
        dst[14] = checked((byte)monitorCount);
    }

    public static void ReadFrame2Header(ReadOnlySpan<byte> src, out int jpegLength, out int width, out int height, out int monitorIndex, out int monitorCount)
    {
        jpegLength = BinaryPrimitives.ReadInt32LittleEndian(src);
        width = BinaryPrimitives.ReadInt32LittleEndian(src[4..]);
        height = BinaryPrimitives.ReadInt32LittleEndian(src[8..]);
        monitorIndex = src[12];
        monitorCount = src[13];
    }
}
