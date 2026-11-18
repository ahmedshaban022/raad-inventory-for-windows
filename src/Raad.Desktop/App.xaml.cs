using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace Raad.Desktop;

public partial class App : Application
{
    internal static readonly Stopwatch StartupClock = Stopwatch.StartNew();
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == "--probe")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var output = Path.GetFullPath(e.Args.ElementAtOrDefault(1) ?? "artifacts/feasibility");
            Directory.CreateDirectory(output);
            try { await FeasibilityProbe.Run(output, e.Args.ElementAtOrDefault(2)); Shutdown(0); }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(output, "probe-error.txt"), error.ToString()); Shutdown(1);
            }
            return;
        }
        var window = new MainWindow(e.Args.FirstOrDefault());
        MainWindow = window; window.Show();
    }
    public static string Assets => Path.Combine(AppContext.BaseDirectory, "Assets");
    internal static void WriteStartupEvidence(string output)
    {
        var process = Process.GetCurrentProcess(); process.Refresh();
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            measuredAtUtc = DateTimeOffset.UtcNow, usableWindowMilliseconds = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds,
            privateBytes = process.PrivateMemorySize64, workingSetBytes = process.WorkingSet64,
            os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(),
            referenceHardwareVerified = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
