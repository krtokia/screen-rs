using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Monitor.Protocol;

namespace Monitor.Sender;

/// <summary>
/// REQUIREMENTS.md §8.1. Captures each monitor separately and JPEG-encodes it, so the receiver can
/// show one monitor at a time at full size. Buffers are reused across frames; at 3 fps the process
/// should sit near zero CPU.
/// </summary>
public sealed class ScreenCapturer : IDisposable
{
    private readonly ImageCodecInfo _jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    private readonly EncoderParameters _encoderParams;
    private readonly MemoryStream _buffer = new(1 << 20);

    private Rectangle[] _bounds = [];
    private Surface[] _surfaces = [];

    private sealed class Surface : IDisposable
    {
        public required Bitmap Raw;
        public required Graphics RawGraphics;
        public Bitmap? Scaled;
        public Graphics? ScaledGraphics;

        public void Dispose()
        {
            ScaledGraphics?.Dispose();
            Scaled?.Dispose();
            RawGraphics.Dispose();
            Raw.Dispose();
        }
    }

    public ScreenCapturer()
    {
        _encoderParams = new EncoderParameters(1);
        _encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)BuildConfig.JpegQuality);
    }

    /// <summary>
    /// Re-reads the monitor layout and returns the monitor count. Call once per tick, before the
    /// CaptureMonitor calls; a runtime display change just changes what the next tick sends.
    /// Monitors are ordered left-to-right (then top-to-bottom), so index 0 is the leftmost one
    /// regardless of which monitor Windows calls primary.
    /// </summary>
    public int RefreshLayout()
    {
        // Screen.AllScreens can return coordinates for monitors left of / above the primary one,
        // where Left and Top go negative — CopyFromScreen takes them as-is.
        var bounds = Screen.AllScreens
            .Select(s => s.Bounds)
            .Where(b => b.Width > 0 && b.Height > 0)
            .OrderBy(b => b.Left).ThenBy(b => b.Top)
            .Take(Wire.MaxMonitors)
            .ToArray();

        if (bounds.Length == 0)
            throw new InvalidOperationException("no monitor has any area");

        if (!bounds.SequenceEqual(_bounds))
            Reallocate(bounds);

        return _bounds.Length;
    }

    /// <summary>
    /// Returns the JPEG bytes for one monitor. The array is the internal buffer — valid only until
    /// the next call, and only the first <c>Length</c> bytes are meaningful.
    /// </summary>
    public (byte[] Buffer, int Length, int Width, int Height) CaptureMonitor(int index)
    {
        var bounds = _bounds[index];
        var surface = _surfaces[index];

        surface.RawGraphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

        var frame = surface.Raw;
        if (surface.Scaled is not null)
        {
            surface.ScaledGraphics!.DrawImage(surface.Raw, 0, 0, surface.Scaled.Width, surface.Scaled.Height);
            frame = surface.Scaled;
        }

        _buffer.SetLength(0);
        frame.Save(_buffer, _jpeg, _encoderParams);

        return (_buffer.GetBuffer(), (int)_buffer.Length, frame.Width, frame.Height);
    }

    /// <summary>The sender never reconnects on a display change; it just starts sending the new layout.</summary>
    private void Reallocate(Rectangle[] bounds)
    {
        DisposeSurfaces();
        _bounds = bounds;
        _surfaces = new Surface[bounds.Length];

        for (var i = 0; i < bounds.Length; i++)
        {
            var raw = new Bitmap(bounds[i].Width, bounds[i].Height, PixelFormat.Format32bppRgb);
            var surface = new Surface { Raw = raw, RawGraphics = Graphics.FromImage(raw) };

            if (BuildConfig.Scale < 1.0f)
            {
                var w = Math.Max(1, (int)(bounds[i].Width * BuildConfig.Scale));
                var h = Math.Max(1, (int)(bounds[i].Height * BuildConfig.Scale));
                surface.Scaled = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                surface.ScaledGraphics = Graphics.FromImage(surface.Scaled);
                surface.ScaledGraphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                surface.ScaledGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            }

            _surfaces[i] = surface;
        }
    }

    private void DisposeSurfaces()
    {
        foreach (var s in _surfaces) s.Dispose();
        _surfaces = [];
        _bounds = [];
    }

    public void Dispose()
    {
        DisposeSurfaces();
        _encoderParams.Dispose();
        _buffer.Dispose();
    }
}
