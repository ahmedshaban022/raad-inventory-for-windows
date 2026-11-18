using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Raad.Engine;

public record TransferManifest(string Product, int FormatVersion, int SchemaVersion, string AppVersion,
    DateTimeOffset CreatedAt, Dictionary<string, string> Checksums);
public record TransferInspection(string StagedDatabase, TransferManifest Manifest, Dictionary<string, long> Summary);

public static class TransferService
{
    public const string AndroidProduct = "raad-inventory-android";
    public const string DatabaseEntry = "raad-inventory.db";
    private const long MaxDatabaseBytes = 512L * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static TransferInspection Inspect(string archive, string stagingRoot)
    {
        var destination = System.IO.Path.Combine(System.IO.Path.GetFullPath(stagingRoot), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            if (zip.Entries.Count != 2 || zip.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2 ||
                zip.Entries.Any(e => e.FullName is not ("manifest.json" or DatabaseEntry)))
                throw new InvalidDataException("ملف النقل يحتوي على مسارات أو ملفات غير متوقعة.");
            var manifestEntry = zip.GetEntry("manifest.json")!;
            var databaseEntry = zip.GetEntry(DatabaseEntry)!;
            if (manifestEntry.Length is < 2 or > 16384 || databaseEntry.Length is < 100 or > MaxDatabaseBytes)
                throw new InvalidDataException("حجم ملف النقل غير مدعوم.");
            using var manifestStream = manifestEntry.Open();
            var manifest = JsonSerializer.Deserialize<TransferManifest>(manifestStream, Json) ?? throw new InvalidDataException("ملف التعريف غير متاح.");
            if (manifest.Product != AndroidProduct || manifest.FormatVersion != 1 || manifest.SchemaVersion != 8 ||
                manifest.Checksums is null || manifest.Checksums.Count != 1 ||
                !manifest.Checksums.TryGetValue(DatabaseEntry, out var expectedHash) || expectedHash is null || expectedHash.Length != 64)
                throw new InvalidDataException("مصدر النقل أو إصدار مخطط رعد غير مدعوم.");
            var dbPath = System.IO.Path.Combine(destination, DatabaseEntry);
            using (var input = databaseEntry.Open())
            using (var output = new FileStream(dbPath, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[128 * 1024]; long copied = 0; int size;
                while ((size = input.Read(buffer)) != 0)
                {
                    copied += size;
                    if (copied > MaxDatabaseBytes) throw new InvalidDataException("ملف النقل أكبر من الحد المدعوم.");
                    output.Write(buffer, 0, size);
                }
                if (copied != databaseEntry.Length) throw new InvalidDataException("ملف النقل غير مكتمل.");
                output.Flush(true);
            }
            using (var payload = File.OpenRead(dbPath))
                if (!Convert.ToHexString(SHA256.HashData(payload)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("بصمة ملف البيانات غير متطابقة.");
            using var connection = new InventoryDatabase(dbPath).Open(true);
            Validate(connection);
            return new(dbPath, manifest, InventoryDatabase.Reconcile(connection));
        }
        catch
        {
            // Only this call's fresh private staging generation can be removed.
            Directory.Delete(destination, true);
            throw;
        }
    }
    public static void Validate(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA trusted_schema=OFF; PRAGMA integrity_check";
        if (!Equals(command.ExecuteScalar(), "ok")) throw new InvalidDataException("قاعدة البيانات تالفة.");
        command.CommandText = "PRAGMA user_version";
        if (Convert.ToInt64(command.ExecuteScalar()) != 8) throw new InvalidDataException("مخطط رعد غير مدعوم.");
        command.CommandText = "PRAGMA foreign_key_check";
        using (var reader = command.ExecuteReader())
            if (reader.Read()) throw new InvalidDataException("علاقات قاعدة البيانات غير سليمة.");
        command.CommandText = "SELECT COUNT(*) FROM dataset WHERE singleton=1 AND currency='EGP' AND length(id)>0";
        if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new InvalidDataException("هذه ليست مجموعة بيانات رعد بالجنيه المصري.");
        foreach (var table in new[] { "dataset", "products", "stock_movements", "operations", "customers", "invoice_draft", "draft_lines", "invoices", "invoice_items", "payments", "payment_reversals", "customer_links", "pending_issue" })
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name=$name";
            command.Parameters.Clear(); command.Parameters.AddWithValue("$name", table);
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
    public static void WriteArchive(string database, string destination, string appVersion = "feasibility-fixture")
    {
        using var input = File.OpenRead(database);
        var hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        using var zip = ZipFile.Open(destination, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(database, DatabaseEntry, CompressionLevel.Fastest);
        using var writer = zip.CreateEntry("manifest.json").Open();
        JsonSerializer.Serialize(writer, new TransferManifest(AndroidProduct, 1, 8, appVersion, DateTimeOffset.UtcNow,
            new() { [DatabaseEntry] = hash }), Json);
    }
}
