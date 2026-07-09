using System.Windows.Forms;

namespace Monitor.Sender;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Only one sender per machine; a second copy would fight the first for the socket.
        using var singleton = new Mutex(initiallyOwned: true, "Global\\ScreenSender.Singleton", out var isFirst);
        if (!isFirst) return;

        // Without this, CopyFromScreen returns a DPI-virtualised (blurry, wrong-sized) image on
        // scaled displays. Must run before any window exists.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();

        Log.Info($"start -> {BuildConfig.ReceiverHost}:{BuildConfig.ReceiverPort} " +
                 $"id={BuildConfig.DeviceId} fps={BuildConfig.Fps} q={BuildConfig.JpegQuality} scale={BuildConfig.Scale}");

        using var stop = new CancellationTokenSource();
        var loop = new SenderLoop(stop.Token);
        var worker = new Thread(loop.Run) { IsBackground = true, Name = "sender" };
        worker.Start();

        using var tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = $"Screen Sender — {BuildConfig.DeviceId}",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) =>
        {
            stop.Cancel();
            Application.Exit();
        });

        Application.Run();

        stop.Cancel();
        worker.Join(TimeSpan.FromSeconds(3));
        Log.Info("stop");
    }
}
