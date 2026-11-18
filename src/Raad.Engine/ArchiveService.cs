using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Raad.Engine;

/// <summary>Which product an archive belongs to.</summary>
public enum ArchiveKind
{
    WindowsBackup,
    AndroidTransfer
}

/// <summary>Manifest stored inside every archive.</summary>
public record ArchiveManifest(string Product, int FormatVersion, int SchemaVersion, string AppVersion,
    DateTimeOffset CreatedAt, Dictionary<string, string> Checksums);

/// <summary>Result of inspecting an archive: what it contains and where the staged copy is.</summary>
public sealed record ArchiveInspection(
    string ArchivePath,
    ArchiveKind Kind,
    string StagedDatabase,
    string AppVersion,
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    Dictionary<string, long> Summary,
    string Currency,
    int MoneyMinorUnits,
    long Sales,
    long Paid,
    long Outstanding,
    long StockTotal,
    long LegacyDeletionTraces,
    string StagingGeneration)
{
    public long Products => Summary.GetValueOrDefault("products");
    public long Customers => Summary.GetValueOrDefault("customers");
    public long Invoices => Summary.GetValueOrDefault("invoices");
}

/// <summary>Outcome of applying an inspected archive to the live data.</summary>
public sealed record ApplyReport(ArchiveInspection Inspection, string SafetyArchive, bool Replaced)
{
    /// <summary>Alias kept for callers that speak of the safety archive as a backup.</summary>
    public string SafetyBackup => SafetyArchive;
}

/// <summary>
/// One archive parser and one safe replacement path, shared by the Windows backup/restore and the
/// Android transfer. The parser is strict, extracts exactly one bounded staging generation, and
/// the replacement publishes a verified safety archive before it swaps data and then verifies the
/// result in both directions.
/// </summary>
public static class ArchiveService
{
    public const int FormatVersion = 1;
    public const int SchemaVersion = 8;
    public const string WindowsProduct = "raad-inventory-windows";
    public const string AndroidProduct = "raad-inventory-android";
    public const string ManifestEntry = "manifest.json";
    public const string DatabaseEntry = "raad-inventory.db";
    public const int MoneyMinorUnits = 2;
    public const string Currency = "EGP";

    private const long MaxManifestBytes = 64 * 1024;
    private const long MaxDatabaseBytes = 512L * 1024 * 1024;
    private const long MinDatabaseBytes = 4096;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Business tables compared in both directions after a replacement.</summary>
    public static readonly string[] VerifiedTables =
    {
        "dataset", "products", "customers", "stock_movements", "invoice_draft", "draft_lines", "invoices",
        "invoice_items", "payments", "payment_reversals", "customer_links", "operations", "pending_issue"
    };

    // ------------------------------------------------------------------ inspect

    /// <summary>
    /// Parses and verifies an archive and stages exactly one bounded, immutable generation.
    /// The staged copy is verified against the checksum, the SQLite integrity, the schema, the
    /// relationships and the domain rules before it may be applied.
    /// </summary>
    public static ArchiveInspection Inspect(string archivePath, ArchiveKind kind, string? stagingRoot = null)
    {
        var root = stagingRoot ?? AppPaths.StagingDirectory;
        Directory.CreateDirectory(root);
        var generation = Path.Combine(root, "gen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(generation);
        try
        {
            var expectedProduct = kind == ArchiveKind.AndroidTransfer ? AndroidProduct : WindowsProduct;
            using var archive = ZipFile.OpenRead(archivePath);
            var entries = archive.Entries;
            if (entries.Count != 2) throw new InvalidDataException("الأرشيف يجب أن يحتوي على ملف التعريف وملف البيانات فقط.");
            var names = entries.Select(entry => entry.FullName).ToList();
            if (names.Any(name => name.Contains("..") || name.StartsWith('/') || name.Contains('\\')))
                throw new InvalidDataException("الأرشيف يحتوي على مسارات غير آمنة.");
            var distinct = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count != 2) throw new InvalidDataException("الأرشيف يحتوي على مدخلات مكررة.");
            if (!distinct.Contains(ManifestEntry) || !distinct.Contains(DatabaseEntry))
                throw new InvalidDataException("محتوى الأرشيف غير متوقع.");

            var manifestEntry = archive.GetEntry(ManifestEntry)!;
            var databaseEntry = archive.GetEntry(DatabaseEntry)!;
            if (manifestEntry.Length is < 2 or > MaxManifestBytes) throw new InvalidDataException("حجم ملف التعريف غير مقبول.");
            if (databaseEntry.Length is < MinDatabaseBytes or > MaxDatabaseBytes) throw new InvalidDataException("حجم ملف البيانات غير مقبول.");

            using var manifestStream = manifestEntry.Open();
            var manifest = JsonSerializer.Deserialize<ArchiveManifest>(manifestStream, Json)
                ?? throw new InvalidDataException("ملف التعريف غير صالح.");
            if (manifest.Product != expectedProduct) throw new InvalidDataException("مصدر الأرشيف من منتج غير مدعوم.");
            if (manifest.FormatVersion != FormatVersion) throw new InvalidDataException("إصدار تنسيق الأرشيف غير مدعوم.");
            if (manifest.SchemaVersion != SchemaVersion) throw new InvalidDataException("إصدار مخطط رعد غير مدعوم.");
            if (manifest.Checksums is null || manifest.Checksums.Count != 1) throw new InvalidDataException("ملف التعريف لا يحمل بصمة واحدة.");
            if (!manifest.Checksums.TryGetValue(DatabaseEntry, out var expectedHash) || expectedHash is null || expectedHash.Length != 64)
                throw new InvalidDataException("بصمة ملف البيانات غير مكتملة.");

            var staged = Path.Combine(generation, DatabaseEntry);
            using (var input = databaseEntry.Open())
            using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[128 * 1024];
                long copied = 0;
                int size;
                while ((size = input.Read(buffer)) != 0)
                {
                    copied += size;
                    if (copied > MaxDatabaseBytes) throw new InvalidDataException("ملف البيانات أكبر من الحد المسموح.");
                    output.Write(buffer, 0, size);
                }
                if (copied != databaseEntry.Length) throw new InvalidDataException("ملف البيانات غير مكتمل.");
                output.Flush(true);
            }
            if (!string.Equals(Hash(staged), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("بصمة ملف البيانات غير مطابقة.");

            using var connection = new InventoryDatabase(staged).Open(true);
            ValidateDatabase(connection);
            var summary = InventoryDatabase.Reconcile(connection);
            var currency = ScalarText(connection, "SELECT currency FROM dataset WHERE singleton=1");
            if (currency != Currency) throw new InvalidDataException("الأرشيف ليس بالجنيه المصري.");
            return new(
                archivePath, kind, staged, manifest.AppVersion, manifest.SchemaVersion, manifest.CreatedAt,
                summary, currency, MoneyMinorUnits,
                summary.GetValueOrDefault("sales"), summary.GetValueOrDefault("paid"),
                summary.GetValueOrDefault("outstanding"), summary.GetValueOrDefault("stock"),
                summary.GetValueOrDefault("legacyDeletionTraces"), generation);
        }
        catch
        {
            TryDelete(generation);
            throw;
        }
    }

    /// <summary>Schema, relationship and domain validation of a staged database.</summary>
    public static void ValidateDatabase(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA trusted_schema=OFF; PRAGMA integrity_check";
        if (!Equals(command.ExecuteScalar(), "ok")) throw new InvalidDataException("قاعدة البيانات تالفة.");
        command.CommandText = "PRAGMA user_version";
        if (Convert.ToInt64(command.ExecuteScalar()) != SchemaVersion) throw new InvalidDataException("مخطط رعد غير مدعوم.");
        command.CommandText = "PRAGMA foreign_key_check";
        using (var reader = command.ExecuteReader())
            if (reader.Read()) throw new InvalidDataException("علاقات قاعدة البيانات غير سليمة.");
        command.CommandText = "SELECT COUNT(*) FROM dataset WHERE singleton=1 AND currency='EGP' AND length(id)>0";
        if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new InvalidDataException("هذه ليست مجموعة بيانات رعد بالجنيه المصري.");
        foreach (var table in VerifiedTables)
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type IN ('table','view') AND name=$name";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$name", table);
            if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new InvalidDataException($"جدول رعد المطلوب غير موجود: {table}");
        }
        command.Parameters.Clear();
        command.CommandText = """
            SELECT COUNT(*) FROM invoices i WHERE i.total != COALESCE((SELECT SUM(l.quantity*l.unit_price) FROM invoice_items l WHERE l.invoice_id=i.id),0)
              OR COALESCE((SELECT SUM(p.amount) FROM payments p WHERE p.invoice_id=i.id AND NOT EXISTS(SELECT 1 FROM payment_reversals r WHERE r.payment_id=p.id)),0)>i.total
            """;
        if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw new InvalidDataException("إجماليات الفواتير أو التحصيلات غير متسقة.");
        command.CommandText = "SELECT COUNT(*) FROM (SELECT SUM(quantity) AS q FROM stock_movements GROUP BY product_id) WHERE q<0 OR q>2147483647";
        if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw new InvalidDataException("رصيد مخزون غير صالح.");
    }

    // ------------------------------------------------------------------ write

    /// <summary>Writes an archive with a manifest and a payload checksum. The file appears atomically.</summary>
    public static string WriteArchive(string databasePath, string destinationPath, ArchiveKind kind, string appVersion)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(destinationPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var product = kind == ArchiveKind.AndroidTransfer ? AndroidProduct : WindowsProduct;
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(databasePath, DatabaseEntry, CompressionLevel.Fastest);
                using var writer = archive.CreateEntry(ManifestEntry).Open();
                var manifest = new ArchiveManifest(product, FormatVersion, SchemaVersion, appVersion, DateTimeOffset.UtcNow,
                    new Dictionary<string, string> { [DatabaseEntry] = Hash(databasePath) });
                JsonSerializer.Serialize(writer, manifest, Json);
            }
            File.Move(temporary, destinationPath, true);
            return destinationPath;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    // ------------------------------------------------------------------ apply

    /// <summary>
    /// The single safe replacement path used by both Windows restore and Android import.
    /// It snapshots the live data with the SQLite backup API, publishes and re-verifies a safety
    /// archive, swaps the data through the SQLite backup API and then verifies every table in both
    /// directions. On any failure the live data is rolled back from the snapshot.
    /// </summary>
    public static ApplyReport Apply(InventoryDatabase live, ArchiveInspection inspection, string safetyDirectory)
    {
        Directory.CreateDirectory(safetyDirectory);
        var work = Path.Combine(Path.GetTempPath(), "raad-apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var snapshot = Path.Combine(work, "live-snapshot.db");
        string? safetyArchive = null;
        try
        {
            // 1. Consistent snapshot of the live data through the SQLite backup API.
            live.Backup(snapshot);

            // 2. Durable, verified safety archive before anything is replaced.
            safetyArchive = Path.Combine(safetyDirectory,
                $"raad-safety-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.raadbackup");
            ArchiveService.WriteArchive(snapshot, safetyArchive, ArchiveKind.WindowsBackup, AppInfo.Version);
            var safetyCheck = Inspect(safetyArchive, ArchiveKind.WindowsBackup, work);
            long snapshotProducts;
            using (var snapshotConnection = new InventoryDatabase(snapshot).Open(true))
                snapshotProducts = InventoryDatabase.Reconcile(snapshotConnection).GetValueOrDefault("products");
            if (safetyCheck.Products != snapshotProducts)
                throw new InvalidOperationException("نسخة الأمان غير مكتملة.");

            // 3. Replace the live content through the SQLite backup API (no manual sidecar handling).
            CopyDatabase(inspection.StagedDatabase, live.Path);

            // 4. Verify both directions before reporting success.
            var differences = Compare(inspection.StagedDatabase, live.Path);
            if (differences.Count > 0)
            {
                CopyDatabase(snapshot, live.Path);
                throw new InvalidOperationException("لم يكتمل التبديل: " + string.Join("، ", differences));
            }
            return new(inspection, safetyArchive, true);
        }
        catch
        {
            // Roll the live data back from the snapshot when the switch did not complete.
            if (File.Exists(snapshot))
            {
                try { CopyDatabase(snapshot, live.Path); } catch { /* keep the safety archive for manual recovery */ }
            }
            throw;
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>Copies one SQLite database into another through the SQLite backup API.</summary>
    public static void CopyDatabase(string sourcePath, string destinationPath)
    {
        using var source = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = Path.GetFullPath(sourcePath), Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        source.Open();
        using var destination = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = Path.GetFullPath(destinationPath), Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        destination.Open();
        source.BackupDatabase(destination);
    }

    /// <summary>
    /// Compares two databases table by table using a hash over every row, so equal row counts with
    /// different identifiers are detected. Returns the names of the differing tables.
    /// </summary>
    public static IReadOnlyList<string> Compare(string leftPath, string rightPath)
    {
        var differences = new List<string>();
        using var left = new InventoryDatabase(leftPath).Open(true);
        using var right = new InventoryDatabase(rightPath).Open(true);
        foreach (var table in VerifiedTables)
        {
            var leftHash = TableHash(left, table);
            var rightHash = TableHash(right, table);
            if (leftHash != rightHash) differences.Add(table);
        }
        return differences;
    }

    private static string TableHash(Microsoft.Data.Sqlite.SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table} ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (reader.Read())
        {
            for (var column = 0; column < reader.FieldCount; column++)
            {
                var value = reader.IsDBNull(column) ? "\u0000" : reader.GetValue(column).ToString() ?? "";
                var type = reader.IsDBNull(column) ? "null" : reader.GetValue(column).GetType().Name;
                hash.AppendData(Encoding.UTF8.GetBytes(type + ":" + value + ""));
            }
            hash.AppendData(Encoding.UTF8.GetBytes(""));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // ------------------------------------------------------------------ helpers

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ScalarText(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    /// <summary>Releases the staged generation once the operator confirmed the replacement.</summary>
    public static void ReleaseStaging(ArchiveInspection inspection) => TryDelete(inspection.StagingGeneration);

    /// <summary>Removes characters that Windows does not allow in a file name.</summary>
    public static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(invalid.Contains(character) || char.IsControl(character) ? '_' : character);
        var result = builder.ToString().Trim().Trim('.');
        return result.Length == 0 ? "invoice" : result;
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch (IOException) { /* keep leftovers for inspection */ }
    }
}
