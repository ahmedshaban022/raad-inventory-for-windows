using System.Text.Json;

namespace Raad.Engine;

public record BackupResult(string Path, long Bytes, DateTimeOffset CreatedAt, bool Verified);

/// <summary>
/// Local backups of the business data. Every archive is written and verified through the shared
/// archive service, and every restore goes through the single safe replacement path.
/// </summary>
public static class BackupService
{
    public const int AutomaticKeep = 7;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private sealed class DailyState
    {
        public string? LastSuccessDay { get; set; }
        public string? LastAttemptDay { get; set; }
        public string? LastFailure { get; set; }
        public string? LastSuccessAt { get; set; }
    }

    /// <summary>Creates a verified backup archive at the exact requested path.</summary>
    public static BackupResult Create(InventoryDatabase database, string destinationPath, bool automatic)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
        Directory.CreateDirectory(directory);
        var staging = Path.Combine(Path.GetTempPath(), "raad-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var snapshot = Path.Combine(staging, ArchiveService.DatabaseEntry);
            database.Backup(snapshot);
            ArchiveService.WriteArchive(snapshot, destinationPath, ArchiveKind.WindowsBackup, AppInfo.Version);

            // Verify the archive that was actually written before it is reported as a success.
            var inspection = ArchiveService.Inspect(destinationPath, ArchiveKind.WindowsBackup, staging);
            if (inspection.Products != InventoryDatabase.Reconcile(new InventoryDatabase(snapshot).Open(true)).GetValueOrDefault("products"))
                throw new InvalidOperationException("النسخة الاحتياطية لم تُكتب كاملة.");
            return new(destinationPath, new FileInfo(destinationPath).Length, inspection.CreatedAt, true);
        }
        finally
        {
            try { Directory.Delete(staging, true); } catch (IOException) { /* leave leftovers behind */ }
        }
    }

    /// <summary>Creates a manual backup with a file name chosen by the operator.</summary>
    public static BackupResult CreateManual(InventoryDatabase database, string destinationPath)
    {
        var full = Path.GetFullPath(destinationPath);
        if (!full.EndsWith(".raadbackup", StringComparison.OrdinalIgnoreCase))
            full = Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileName(full) + ".raadbackup");
        return Create(database, full, automatic: false);
    }

    /// <summary>Keeps only the newest verified automatic backups; manual and safety archives are never deleted.</summary>
    public static int PruneAutomatic(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        var files = new DirectoryInfo(directory).GetFiles("raad-backup-*-auto.raadbackup")
            .OrderByDescending(file => file.Name, StringComparer.Ordinal).ToList();
        var removed = 0;
        for (var index = AutomaticKeep; index < files.Count; index++)
        {
            try { files[index].Delete(); removed++; }
            catch (IOException) { /* keep the file if the disk refuses deletion */ }
        }
        return removed;
    }

    /// <summary>
    /// Creates the single successful automatic backup of the local day. A failed attempt is retried
    /// on the next launch and after a day rollover, and the state keeps the true last success.
    /// </summary>
    public static BackupResult? EnsureDaily(InventoryDatabase database, string directory)
    {
        Directory.CreateDirectory(directory);
        var stateFile = Path.Combine(directory, "daily-backup-state.json");
        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        DailyState state;
        try
        {
            state = File.Exists(stateFile)
                ? JsonSerializer.Deserialize<DailyState>(File.ReadAllText(stateFile), Json) ?? new DailyState()
                : new DailyState();
        }
        catch (Exception) { state = new DailyState(); }

        if (state.LastSuccessDay == today) return null;

        var destination = Path.Combine(directory, $"raad-backup-{DateTime.Now:yyyyMMdd-HHmmss}-auto.raadbackup");
        state.LastAttemptDay = today;
        try
        {
            var result = Create(database, destination, automatic: true);
            state.LastSuccessDay = today;
            state.LastSuccessAt = DateTimeOffset.UtcNow.ToString("O");
            state.LastFailure = null;
            PruneAutomatic(directory);
            WriteState(stateFile, state);
            return result;
        }
        catch (Exception error)
        {
            state.LastFailure = error.Message;
            WriteState(stateFile, state);
            throw;
        }
    }

    /// <summary>Reads the true last successful automatic backup time, or null when there is none.</summary>
    public static DateTimeOffset? LastDailySuccess(string directory)
    {
        var stateFile = Path.Combine(directory, "daily-backup-state.json");
        if (!File.Exists(stateFile)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<DailyState>(File.ReadAllText(stateFile), Json);
            return state?.LastSuccessAt is null ? null : DateTimeOffset.Parse(state.LastSuccessAt);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Inspects an archive before it is applied. Shared with the Android transfer path.</summary>
    public static ArchiveInspection Inspect(string archivePath, ArchiveKind kind = ArchiveKind.WindowsBackup) =>
        ArchiveService.Inspect(archivePath, kind, AppPaths.StagingDirectory);

    /// <summary>Restores the live data from an inspected archive through the single safe path.</summary>
    public static ApplyReport Restore(InventoryDatabase database, string archivePath, string safetyDirectory)
    {
        var inspection = Inspect(archivePath);
        return ArchiveService.Apply(database, inspection, safetyDirectory);
    }

    private static void WriteState(string path, DailyState state)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, Json));
        File.Move(temporary, path, true);
    }
}
