using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Monitor.Sender;

/// <summary>
/// REQUIREMENTS.md §8.1. Captures the whole Virtual Screen (all monitors merged) and JPEG-encodes it.
/// Buffers are reused across frames; at 3 fps the process should sit near zero CPU.
/// </summary>
public sealed class ScreenCapturer : IDisposable
{
    private readonly ImageCodecInfo _jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    private readonly EncoderParameters _encoderParams;
    private readonly MemoryStream _buffer = new(1 << 20);

    private Rectangle _bounds;
    private Bitmap? _raw;
    private Graphics? _rawGraphics;
    private Bitmap? _scaled;
    private Graphics? _scaledGraphics;

    public ScreenCapturer()
    {
        _encoderParams = new EncoderParameters(1);
        _encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)BuildConfig.JpegQuality);
    }

    /// <summary>
    /// Returns the JPEG bytes for the current screen. The array is the internal buffer — valid only
    /// until the next call, and only the first <c>Length</c> bytes are meaningful.
    /// </summary>
    public (byte[] Buffer, int Length, int Width, int Height) CaptureJpeg()
    {
        // SystemInformation.VirtualScreen already accounts for monitors placed left of / above the
        // primary one, where Left and Top go negative.
        var bounds = SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException($"Virtual screen has no area: {bounds}");

        if (_raw is null || bounds != _bounds)
            Reallocate(bounds);

        _rawGraphics!.CopyFromScreen(_bounds.Left, _bounds.Top, 0, 0, _bounds.Size, CopyPixelOperation.SourceCopy);

        var frame = _raw!;
        if (_scaled is not null)
        {
            _scaledGraphics!.DrawImage(_raw!, 0, 0, _scaled.Width, _scaled.Height);
            frame = _scaled;
        }

        _buffer.SetLength(0);
        frame.Save(_buffer, _jpeg, _encoderParams);

        return (_buffer.GetBuffer(), (int)_buffer.Length, frame.Width, frame.Height);
    }

    /// <summary>The sender never reconnects on a resolution change; it just starts sending a new size.</summary>
    private void Reallocate(Rectangle bounds)
    {
        DisposeSurfaces();
        _bounds = bounds;

        _raw = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        _rawGraphics = Graphics.FromImage(_raw);

        if (BuildConfig.Scale < 1.0f)
        {
            var w = Math.Max(1, (int)(bounds.Width * BuildConfig.Scale));
            var h = Math.Max(1, (int)(bounds.Height * BuildConfig.Scale));
            _scaled = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            _scaledGraphics = Graphics.FromImage(_scaled);
            _scaledGraphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            _scaledGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }
    }

    private void DisposeSurfaces()
    {
        _scaledGraphics?.Dispose();
        _scaled?.Dispose();
        _rawGraphics?.Dispose();
        _raw?.Dispose();

        _scaledGraphics = null;
        _scaled = null;
        _rawGraphics = null;
        _raw = null;
    }

    public void Dispose()
    {
        DisposeSurfaces();
        _encoderParams.Dispose();
        _buffer.Dispose();
    }
}
