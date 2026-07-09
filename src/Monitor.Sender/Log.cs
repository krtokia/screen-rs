using System.Text;

namespace Monitor.Sender;

/// <summary>
/// REQUIREMENTS.md §13.2. The sender is unreachable, so a log is the only forensic trail — but it is
/// also unattended for months, so it must never grow without bound. One file, one backup, hard capped.
/// Logging failures are swallowed: a full disk must not take the sender down.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1 << 20;

    private static readonly object Gate = new();
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenSender");
    private static readonly string Path1 = Path.Combine(Dir, "sender.log");
    private static readonly string Path2 = Path.Combine(Dir, "sender.log.1");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}";

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);

                if (File.Exists(Path1) && new FileInfo(Path1).Length >= MaxBytes)
                    File.Move(Path1, Path2, overwrite: true);

                File.AppendAllText(Path1, line, Encoding.UTF8);
            }
            catch
            {
                // Disk full, permissions, whatever. Losing a log line is always better than exiting.
            }
        }
    }
}
