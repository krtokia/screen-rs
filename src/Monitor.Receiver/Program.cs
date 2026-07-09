using System.Globalization;
using System.Reflection;
using System.Windows.Forms;
using Monitor.Protocol;

namespace Monitor.Receiver;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.Run(new MainForm(ResolvePort(args)));
    }

    /// <summary>Baked at build time; an argv override exists only to make local testing painless.</summary>
    private static int ResolvePort(string[] args)
    {
        if (args.Length > 0 && int.TryParse(args[0], out var fromArgs))
            return fromArgs;

        foreach (var a in typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            if (a.Key == "ListenPort" &&
                int.TryParse(a.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromBuild))
                return fromBuild;

        return Wire.DefaultPort;
    }
}
