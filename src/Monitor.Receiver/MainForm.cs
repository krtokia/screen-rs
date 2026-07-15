using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Monitor.Receiver;

public sealed class MainForm : Form
{
    private readonly ReceiverServer _server;
    private readonly object _gate = new();

    /// <summary>
    /// Latest frame per monitor, per sender. Entries survive a disconnect on purpose: the last
    /// picture stays on screen (title says the sender is gone) instead of flashing to black on
    /// every reconnect.
    /// </summary>
    private sealed class SenderState
    {
        public Bitmap?[] Frames = new Bitmap?[1];
        public string Info = "";
    }

    private readonly Dictionary<string, SenderState> _senders = new(StringComparer.Ordinal);

    private string? _selectedDevice;
    private int _selectedMonitor = All;
    private const int All = -1;
    private string _status = "starting";

    private readonly ToolStrip _toolbar;
    private readonly ToolStripComboBox _deviceBox;
    private readonly ToolStripLabel _deviceLabel;
    private readonly Viewport _viewport;
    private bool _syncingToolbar;

    /// <summary>Gap drawn between monitors in the "all" view, so two dark consoles don't blur into one.</summary>
    private const int MonitorGap = 2;

    public MainForm(int port)
    {
        // Must exist before any property that can fire OnResize (ClientSize does, from inside
        // the ctor) — OnResize dereferences _server.
        _server = new ReceiverServer(port);
        _server.FrameReceived += OnFrame;
        _server.SendersChanged += OnSendersChanged;
        _server.StatusChanged += OnStatus;

        Text = "Screen Receiver";
        BackColor = Color.Black;
        ClientSize = new Size(1280, 720);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        _deviceLabel = new ToolStripLabel("Sender:");
        _deviceBox = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 180 };
        _deviceBox.SelectedIndexChanged += OnDeviceBoxChanged;

        _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        _toolbar.Items.Add(_deviceLabel);
        _toolbar.Items.Add(_deviceBox);
        _toolbar.Items.Add(new ToolStripSeparator());

        _viewport = new Viewport { Dock = DockStyle.Fill, BackColor = Color.Black };
        _viewport.Paint += PaintViewport;

        Controls.Add(_viewport);
        Controls.Add(_toolbar);

        _server.Start();
    }

    private sealed class Viewport : Panel
    {
        public Viewport()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }
    }

    // ---- server events (off the UI thread) ------------------------------------------------------

    private void OnFrame(string deviceId, int monitorIndex, int monitorCount, Bitmap frame)
    {
        var structureChanged = false;

        lock (_gate)
        {
            if (!_senders.TryGetValue(deviceId, out var state))
            {
                _senders[deviceId] = state = new SenderState();
                structureChanged = true;
            }

            if (state.Frames.Length != monitorCount)
            {
                foreach (var b in state.Frames) b?.Dispose();
                state.Frames = new Bitmap?[monitorCount];
                structureChanged = true;
            }

            state.Frames[monitorIndex]?.Dispose();
            state.Frames[monitorIndex] = frame;
            state.Info = monitorCount > 1 ? $"{monitorCount} monitors {frame.Width}x{frame.Height}" : $"{frame.Width}x{frame.Height}";
        }

        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(() => AfterFrame(deviceId, structureChanged)); } catch (ObjectDisposedException) { }
    }

    private void OnSendersChanged()
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(() => { ApplyPausePolicy(); UpdateTitle(); }); } catch (ObjectDisposedException) { }
    }

    private void OnStatus(string status)
    {
        _status = status;
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(() => { UpdateTitle(); _viewport.Invalidate(); }); } catch (ObjectDisposedException) { }
    }

    // ---- UI thread -------------------------------------------------------------------------------

    private void AfterFrame(string deviceId, bool structureChanged)
    {
        if (_selectedDevice is null)
        {
            SelectDevice(deviceId);
            return;
        }

        if (structureChanged) SyncToolbar();

        if (string.Equals(deviceId, _selectedDevice, StringComparison.Ordinal))
        {
            _viewport.Invalidate();
            UpdateTitle();
        }
    }

    private void SelectDevice(string deviceId)
    {
        _selectedDevice = deviceId;
        _selectedMonitor = All;
        SyncToolbar();
        ApplyPausePolicy();
        _viewport.Invalidate();
        UpdateTitle();
    }

    private void OnDeviceBoxChanged(object? sender, EventArgs e)
    {
        if (_syncingToolbar || _deviceBox.SelectedItem is not string deviceId) return;
        SelectDevice(deviceId);
    }

    /// <summary>Rebuilds the device combo and the monitor buttons ("All", "1".."N") from current state.</summary>
    private void SyncToolbar()
    {
        _syncingToolbar = true;
        try
        {
            string[] devices;
            int monitorCount;
            lock (_gate)
            {
                devices = _senders.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
                monitorCount = _selectedDevice is not null && _senders.TryGetValue(_selectedDevice, out var s) ? s.Frames.Length : 1;
            }

            if (!devices.SequenceEqual(_deviceBox.Items.Cast<string>()))
            {
                _deviceBox.Items.Clear();
                _deviceBox.Items.AddRange(devices);
            }
            _deviceBox.SelectedItem = _selectedDevice;

            // Everything after the separator is a monitor button; rebuild that tail.
            var fixedItems = 3; // label, combo, separator
            while (_toolbar.Items.Count > fixedItems) _toolbar.Items.RemoveAt(fixedItems);

            if (monitorCount > 1)
            {
                if (_selectedMonitor >= monitorCount) _selectedMonitor = All;

                AddMonitorButton("All", All);
                for (var i = 0; i < monitorCount; i++)
                    AddMonitorButton((i + 1).ToString(), i);
            }
            else
            {
                _selectedMonitor = All;
            }
        }
        finally
        {
            _syncingToolbar = false;
        }
    }

    private void AddMonitorButton(string text, int monitor)
    {
        var button = new ToolStripButton(text)
        {
            Checked = _selectedMonitor == monitor,
            ToolTipText = monitor == All ? "Show every monitor (key: 0)" : $"Show only monitor {monitor + 1} (key: {monitor + 1})",
        };
        button.Click += (_, _) => SelectMonitor(monitor);
        _toolbar.Items.Add(button);
    }

    private void SelectMonitor(int monitor)
    {
        _selectedMonitor = monitor;
        foreach (var item in _toolbar.Items.OfType<ToolStripButton>())
            item.Checked = string.Equals(item.Text, monitor == All ? "All" : (monitor + 1).ToString(), StringComparison.Ordinal);
        _viewport.Invalidate();
        UpdateTitle();
    }

    /// <summary>0 = all monitors, 1..9 = that monitor. Works no matter what has focus.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        var key = e.KeyCode is >= Keys.NumPad0 and <= Keys.NumPad9 ? e.KeyCode - Keys.NumPad0 : e.KeyCode - Keys.D0;
        if (key is < 0 or > 9) return;

        int monitorCount;
        lock (_gate)
        {
            monitorCount = _selectedDevice is not null && _senders.TryGetValue(_selectedDevice, out var s) ? s.Frames.Length : 1;
        }

        if (key == 0) SelectMonitor(All);
        else if (key <= monitorCount && monitorCount > 1) SelectMonitor(key - 1);
        e.Handled = true;
    }

    private void UpdateTitle()
    {
        var connected = _server.ConnectedSenders;

        if (_selectedDevice is null)
        {
            Text = $"Screen Receiver — {_status}";
            return;
        }

        string info;
        lock (_gate)
        {
            info = _senders.TryGetValue(_selectedDevice, out var s) ? s.Info : "";
        }

        var monitor = _selectedMonitor == All ? "" : $" [monitor {_selectedMonitor + 1}]";
        var live = connected.Contains(_selectedDevice) ? "" : " — OFFLINE, showing last frame";
        var others = connected.Count > 1 ? $" — {connected.Count} senders connected" : "";
        Text = $"Screen Receiver — {_selectedDevice}{monitor} {info}{live}{others}";
    }

    /// <summary>
    /// §8.2. Exactly one sender streams: the selected one, and only while the window is visible.
    /// Everything else gets PAUSE, which is what keeps every remote machine's idle CPU at zero.
    /// </summary>
    private void ApplyPausePolicy()
    {
        var minimized = WindowState == FormWindowState.Minimized;
        foreach (var deviceId in _server.ConnectedSenders)
            _server.SetPaused(deviceId, minimized || !string.Equals(deviceId, _selectedDevice, StringComparison.Ordinal));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // WinForms raises OnResize from inside our own ctor; guard against any field not yet set.
        if (_server is null) return;
        ApplyPausePolicy();
    }

    // ---- painting --------------------------------------------------------------------------------

    private void PaintViewport(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);
        var client = _viewport.ClientSize;

        lock (_gate)
        {
            if (_selectedDevice is null || !_senders.TryGetValue(_selectedDevice, out var state))
            {
                DrawCentered(g, _status, client);
                return;
            }

            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            if (_selectedMonitor != All)
            {
                var frame = _selectedMonitor < state.Frames.Length ? state.Frames[_selectedMonitor] : null;
                if (frame is null) DrawCentered(g, $"no frame yet from monitor {_selectedMonitor + 1}", client);
                else g.DrawImage(frame, FitRect(frame.Size, client));
                return;
            }

            var frames = state.Frames.Where(f => f is not null).Cast<Bitmap>().ToArray();
            if (frames.Length == 0)
            {
                DrawCentered(g, _status, client);
                return;
            }

            if (frames.Length == 1)
            {
                g.DrawImage(frames[0], FitRect(frames[0].Size, client));
                return;
            }

            // Side-by-side composite, ordered by monitor index (index 0 is the sender's leftmost
            // monitor). Heights may differ; each frame is vertically centered.
            var totalWidth = frames.Sum(f => f.Width);
            var maxHeight = frames.Max(f => f.Height);
            var gaps = MonitorGap * (frames.Length - 1);
            var availableWidth = Math.Max(1, client.Width - gaps);

            var scale = Math.Min((double)availableWidth / totalWidth, (double)client.Height / maxHeight);
            var x = (client.Width - (int)(totalWidth * scale) - gaps) / 2;

            foreach (var frame in frames)
            {
                var w = Math.Max(1, (int)(frame.Width * scale));
                var h = Math.Max(1, (int)(frame.Height * scale));
                g.DrawImage(frame, new Rectangle(x, (client.Height - h) / 2, w, h));
                x += w + MonitorGap;
            }
        }
    }

    private void DrawCentered(Graphics g, string text, Size client) =>
        TextRenderer.DrawText(g, text, Font, new Rectangle(Point.Empty, client), Color.Gray,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

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
        lock (_gate)
        {
            foreach (var s in _senders.Values)
                foreach (var b in s.Frames) b?.Dispose();
            _senders.Clear();
        }
        base.OnFormClosed(e);
    }
}
