using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Raad.Engine;

namespace Raad.Desktop;

internal static class FeasibilityProbe
{
    public static async Task Run(string output, string? databasePath)
    {
        var process = Process.GetCurrentProcess();
        var documents = new List<object>();
        foreach (var count in new[] { 1, 30, 100, 300 })
        {
            var path = Path.Combine(output, $"invoice-{count}.pdf");
            var watch = Stopwatch.StartNew();
            await Task.Run(() => InvoiceDocument.Save(InvoiceSnapshot.Fixture(count), path, App.Assets));
            var generationMs = watch.Elapsed.TotalMilliseconds;
            using var pdf = await PdfPages.Open(path);
            var images = Path.Combine(output, $"images-{count}-{Guid.NewGuid().ToString("N")[..8]}");
            await pdf.ExportImages(images, $"invoice-{count}", false);
            await pdf.ExportImages(images, $"invoice-{count}", true);
            process.Refresh();
            documents.Add(new { lines = count, pages = pdf.Count, pdf = path, images, generationMs,
                completeOutputMs = watch.Elapsed.TotalMilliseconds, privateBytes = process.PrivateMemorySize64,
                peakWorkingSetBytes = process.PeakWorkingSet64 });
        }
        object? database = null;
        if (databasePath is not null)
        {
            var inventory = new InventoryDatabase(databasePath);
            var samples = new List<double>();
            foreach (var query in Enumerable.Range(0, 100).Select(i => i % 3 == 0 ? "فرامل" : i % 3 == 1 ? "r3d" : "منتج"))
            {
                var watch = Stopwatch.StartNew(); inventory.Search(query); samples.Add(watch.Elapsed.TotalMilliseconds);
            }
            samples.Sort();
            using var connection = inventory.Open(true);
            var summary = InventoryDatabase.Reconcile(connection);
            var backupPath = Path.Combine(output, "consistent-backup.db"); inventory.Backup(backupPath);
            database = new { path = inventory.Path, summary, searchP95Ms = samples[94], searchMaxMs = samples[^1], samples = samples.Count, backupPath };
        }
        File.WriteAllText(Path.Combine(output, "probe-results.json"), JsonSerializer.Serialize(new
        {
            build = "0.1.0-feasibility", measuredAtUtc = DateTimeOffset.UtcNow, runtime = Environment.Version.ToString(),
            os = Environment.OSVersion.ToString(), machine = Environment.MachineName,
            hardwareQualification = "development-host-only; Windows10, 4GB/HDD and physical print pending", documents, database
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
