using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Monitor.Receiver;

public sealed class MainForm : Form
{
    private readonly ReceiverServer _server;
    private readonly object _gate = new();

    private Bitmap? _frame;
    private string _status = "starting";
    private string _frameInfo = "";

    public MainForm(int port)
    {
        // Must exist before any property that can fire OnResize (ClientSize does, from inside
        // the ctor) — OnResize dereferences _server.
        _server = new ReceiverServer(port);
        _server.FrameReceived += OnFrame;
        _server.StatusChanged += OnStatus;

        Text = "Screen Receiver";
        BackColor = Color.Black;
        ClientSize = new Size(1280, 720);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

        _server.Start();
    }

    /// <summary>Off the UI thread. Keep only the newest frame; an older one is worthless.</summary>
    private void OnFrame(Bitmap frame, string info)
    {
        lock (_gate)
        {
            _frame?.Dispose();
            _frame = frame;
            _frameInfo = info;
        }

        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(Invalidate); } catch (ObjectDisposedException) { }
    }

    private void OnStatus(string status)
    {
        _status = status;
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(UpdateTitle); } catch (ObjectDisposedException) { }
    }

    private void UpdateTitle()
    {
        Text = _frameInfo.Length > 0 ? $"Screen Receiver — {_status} — {_frameInfo}" : $"Screen Receiver — {_status}";
        Invalidate();
    }

    /// <summary>
    /// §8.2. Minimised means nobody is looking, so tell the sender to stop capturing entirely.
    /// This is what keeps the remote machine's idle CPU at zero.
    /// </summary>
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // WinForms raises OnResize from inside our own ctor; guard against any field not yet set.
        if (_server is null) return;
        _server.SetPaused(WindowState == FormWindowState.Minimized);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);

        lock (_gate)
        {
            if (_frame is null)
            {
                TextRenderer.DrawText(g, _status, Font, ClientRectangle, Color.Gray,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(_frame, FitRect(_frame.Size, ClientSize));
        }
    }

    /// <summary>Letterboxed fit — never distorts the sender's aspect ratio.</summary>
    private static Rectangle FitRect(Size image, Size client)
    {
        if (image.Width <= 0 || image.Height <= 0 || client.Width <= 0 || client.Height <= 0)
            return Rectangle.Empty;

        var scale = Math.Min((double)client.Width / image.Width, (double)client.Height / image.Height);
        var w = Math.Max(1, (int)(image.Width * scale));
        var h = Math.Max(1, (int)(image.Height * scale));
        return new Rectangle((client.Width - w) / 2, (client.Height - h) / 2, w, h);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _server.Dispose();
        lock (_gate) { _frame?.Dispose(); _frame = null; }
        base.OnFormClosed(e);
    }
}
