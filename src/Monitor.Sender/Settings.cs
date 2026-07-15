using System.Text;
using Monitor.Protocol;

namespace Monitor.Sender;

/// <summary>The three values that differ per sender machine. Everything else stays build-time (§6.1).</summary>
public sealed record SenderSettings(string ReceiverHost, int ReceiverPort, string DeviceId)
{
    public static bool IsValidDeviceId(string id) =>
        id.Length > 0 && Encoding.UTF8.GetByteCount(id) <= Wire.DeviceIdBytes;
}

/// <summary>
/// REQUIREMENTS.md §6.1. Runtime overrides for receiver host/port and device id, written only by a
/// human action (the install dialog or the tray settings menu) and read once at startup. Anything
/// missing or invalid silently falls back to the build-time values — a corrupt file must never keep
/// the sender from coming up unattended (§4), and no code path here ever throws or prompts.
/// </summary>
public static class Settings
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenSender", "settings.ini");

    private static volatile SenderSettings _current = Load();

    public static SenderSettings Current => _current;

    public static SenderSettings Load()
    {
        var host = BuildConfig.ReceiverHost;
        var port = BuildConfig.ReceiverPort;
        var deviceId = BuildConfig.DeviceId;

        try
        {
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var split = line.IndexOf('=');
                if (split <= 0) continue;

                var key = line[..split].Trim();
                var value = line[(split + 1)..].Trim();

                // Invalid values are skipped per-field, keeping the baked default for that field only.
                switch (key.ToLowerInvariant())
                {
                    case "receiverhost" when value.Length > 0:
                        host = value;
                        break;
                    case "receiverport" when int.TryParse(value, out var p) && p is >= 1 and <= 65535:
                        port = p;
                        break;
                    case "deviceid" when SenderSettings.IsValidDeviceId(value):
                        deviceId = value;
                        break;
                }
            }
        }
        catch
        {
            // No file (the normal first-install state) or unreadable: baked-in defaults.
        }

        return new SenderSettings(host, port, deviceId);
    }

    /// <summary>
    /// Applies to this process and persists. Returns false when the file cannot be written — the
    /// new values still hold until the process exits, so the operator's change works immediately
    /// and the failure is only about surviving a restart.
    /// </summary>
    public static bool Apply(SenderSettings settings)
    {
        _current = settings;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllLines(FilePath,
            [
                $"ReceiverHost={settings.ReceiverHost}",
                $"ReceiverPort={settings.ReceiverPort}",
                $"DeviceId={settings.DeviceId}",
            ]);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
