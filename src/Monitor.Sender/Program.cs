using System.Windows.Forms;

namespace Monitor.Sender;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // "--run" skips self-install and runs in place — used for local testing and by nothing else.
        var runInPlace = args.Any(a => string.Equals(a, "--run", StringComparison.OrdinalIgnoreCase));

        // Without this, CopyFromScreen returns a DPI-virtualised (blurry, wrong-sized) image on
        // scaled displays. Must run before any window exists — including the install-time
        // settings dialog below.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();

        // Self-install must happen BEFORE the singleton mutex. The installer hands off to the installed
        // copy, which needs to claim that mutex itself; if the installer held it, the copy would die.
        if (!runInPlace && !SelfInstall.EnsureInstalledAndRunningFromTarget())
            return; // acted as installer; the installed copy is now running.

        // Only one sender per machine; a second copy would fight the first for the socket.
        using var singleton = new Mutex(initiallyOwned: true, "Global\\ScreenSender.Singleton", out var isFirst);
        if (!isFirst) return;

        Log.Info($"start -> {Settings.Current.ReceiverHost}:{Settings.Current.ReceiverPort} " +
                 $"id={Settings.Current.DeviceId} fps={BuildConfig.Fps} q={BuildConfig.JpegQuality} scale={BuildConfig.Scale}");

        using var stop = new CancellationTokenSource();
        var loop = new SenderLoop(stop.Token);
        var worker = new Thread(loop.Run) { IsBackground = true, Name = "sender" };
        worker.Start();

        using var tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = $"Screen Sender — {Settings.Current.DeviceId}",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        tray.ContextMenuStrip.Items.Add("설정...", null, (_, _) =>
        {
            using var dialog = new SettingsDialog(Settings.Current);
            if (dialog.ShowDialog() != DialogResult.OK) return;

            if (!Settings.Apply(dialog.Value))
                MessageBox.Show(
                    $"설정 파일 저장에 실패했습니다. 지금 세션에는 적용되지만 재부팅하면 이전 값으로 돌아갑니다.\n\n{Settings.FilePath}",
                    "Screen Sender", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            var s = Settings.Current;
            tray.Text = $"Screen Sender — {s.DeviceId}";
            Log.Info($"settings changed -> {s.ReceiverHost}:{s.ReceiverPort} id={s.DeviceId}");
            loop.Reconnect();
        });
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
