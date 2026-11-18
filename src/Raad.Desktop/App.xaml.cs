using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace Raad.Desktop;

public partial class App : Application
{
    internal static readonly Stopwatch StartupClock = Stopwatch.StartNew();

    /// <summary>
    /// Raised when the Windows session is locked (or a remote session is lost) so the main
    /// window can hide the business data.
    /// </summary>
    public static event Action? PrivacyLockRequested;

    static App() => AppHost.PrivacyLockRequested += () => PrivacyLockRequested?.Invoke();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var arguments = e.Args ?? Array.Empty<string>();
        var isProbe = arguments.Length > 0 && string.Equals(arguments[0], "--probe", StringComparison.Ordinal);

        // The QA root is resolved first so the single-instance guard can scope its mutex to the
        // QA root and a test run never collides with the live instance of the same Windows user.
        if (!AppHost.TryReadQaRoot(arguments, out var qaRoot, out var qaError))
        {
            Refuse(qaError!, isProbe, arguments);
            return;
        }
        if (qaRoot is not null && !AppHost.TryApplyQaRoot(qaRoot))
        {
            Refuse(AppHost.RefusalMessage, isProbe, arguments);
            return;
        }

        // The probe writes its own evidence and shuts down; it must never be blocked by a
        // running instance, so the single-instance guard only applies to a normal start.
        if (!isProbe && !AppHost.TryAcquireSingleInstance())
        {
            MessageBox.Show("رعد يعمل بالفعل", "رعد", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        if (isProbe)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // Best effort only: a running instance does not stop the probe.
            AppHost.TryAcquireSingleInstance();
            var output = Path.GetFullPath(arguments.ElementAtOrDefault(1) ?? "artifacts/feasibility");
            Directory.CreateDirectory(output);
            try { await FeasibilityProbe.Run(output, arguments.ElementAtOrDefault(2)); Shutdown(0); }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(output, "probe-error.txt"), error.ToString()); Shutdown(1);
            }
            return;
        }

        AppHost.SubscribeSessionEvents();

        var window = new MainWindow();
        MainWindow = window; window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppHost.UnsubscribeSessionEvents();
        AppHost.ReleaseSingleInstance();
        base.OnExit(e);
    }

    /// <summary>
    /// Stops a refused start. A probe run records the reason in its output folder instead of
    /// showing a modal box, so an automated probe never waits for a click.
    /// </summary>
    private void Refuse(string message, bool isProbe, string[] arguments)
    {
        if (isProbe)
        {
            try
            {
                var output = Path.GetFullPath(arguments.ElementAtOrDefault(1) ?? "artifacts/feasibility");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "probe-error.txt"), message);
            }
            catch (Exception) { /* the refusal is still reported through the exit code */ }
            Shutdown(2);
            return;
        }
        MessageBox.Show(message, "رعد", MessageBoxButton.OK, MessageBoxImage.Warning);
        Shutdown(2);
    }

    public static string Assets => Path.Combine(AppContext.BaseDirectory, "Assets");
    internal static void WriteStartupEvidence(string output)
    {
        var process = Process.GetCurrentProcess(); process.Refresh();
        var path = output;
        if (AppHost.IsQaMode && AppHost.IsInsideLiveRoot(path))
        {
            // A QA run must never write into the live data folder.
            path = Path.Combine(AppHost.QaRoot!, "logs", "startup.json");
        }
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            measuredAtUtc = DateTimeOffset.UtcNow, usableWindowMilliseconds = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds,
            privateBytes = process.PrivateMemorySize64, workingSetBytes = process.WorkingSet64,
            os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(),
            referenceHardwareVerified = false,
            qaMode = AppHost.IsQaMode, dataRoot = AppHost.DataRoot,
            singleInstance = AppHost.SingleInstanceHeld, previewBuild = true
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
