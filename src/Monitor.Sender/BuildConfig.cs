using System.Globalization;
using System.Reflection;
using Monitor.Protocol;

namespace Monitor.Sender;

/// <summary>
/// REQUIREMENTS.md §6.1. Baked in by Monitor.Sender.csproj at build time.
/// Nothing here is read from disk, argv, or the registry — the sender has no runtime configuration.
/// </summary>
public static class BuildConfig
{
    public static string ReceiverHost { get; } = Meta("ReceiverHost", "127.0.0.1");
    public static int ReceiverPort { get; } = Int("ReceiverPort", Wire.DefaultPort);
    public static string DeviceId { get; } = Meta("DeviceId", "SENDER-01");
    public static int Fps { get; } = Math.Clamp(Int("CaptureFps", 3), 1, 30);
    public static int JpegQuality { get; } = Math.Clamp(Int("JpegQuality", 60), 1, 100);
    public static float Scale { get; } = Math.Clamp(Single("CaptureScale", 1.0f), 0.1f, 1.0f);

    public static TimeSpan FrameInterval => TimeSpan.FromSeconds(1.0 / Fps);

    // §13.5 — the receiver may be off for hours; back off instead of hammering connect().
    public static readonly TimeSpan ReconnectMin = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ReconnectMax = TimeSpan.FromSeconds(60);

    private static string Meta(string key, string fallback)
    {
        foreach (var a in typeof(BuildConfig).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            if (a.Key == key && !string.IsNullOrWhiteSpace(a.Value))
                return a.Value;
        return fallback;
    }

    private static int Int(string key, int fallback) =>
        int.TryParse(Meta(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static float Single(string key, float fallback) =>
        float.TryParse(Meta(key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
