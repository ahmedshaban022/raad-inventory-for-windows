using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Raad.Engine;
using Xunit;

namespace Raad.Engine.Tests;

/// <summary>
/// Regression tests for the archive parser and the single safe replacement path used by both the
/// Windows restore and the Android import.
/// </summary>
public sealed class ArchiveTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "raad-archive-" + Guid.NewGuid().ToString("N"));
    private readonly string livePath;
    private readonly InventoryDatabase live;

    public ArchiveTests()
    {
        Directory.CreateDirectory(root);
        livePath = Path.Combine(root, "live", "raad-inventory.db");
        Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
        var store = Store.Open(livePath);
        live = store.Database;
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { /* best effort */ }
    }

    private string SeedLive()
    {
        var store = Store.Open(livePath);
        store.SaveProduct(null, "صنف المصدر", "100", "1", "10");
        var product = store.Products("", false, 0).Single();
        store.AddDraftLine(product.Id);
        store.SetDraftLine(product.Id, "2", "100");
        store.SetDraftPayment("50");
        store.SaveCustomer(null, "عميل الاختبار", "");
        store.SetDraftCustomer(store.Customers("", false, 0).Single().Id);
        var issued = store.IssueDraft();
        return issued.Reference;
    }

    private string BuildArchive(ArchiveKind kind, string sourceDatabase, string name)
    {
        var destination = Path.Combine(root, name);
        ArchiveService.WriteArchive(sourceDatabase, destination, kind, "test");
        return destination;
    }

    private string BuildStandalone(string productName, string price)
    {
        var path = Path.Combine(root, "standalone-" + Guid.NewGuid().ToString("N") + ".db");
        var store = Store.Open(path);
        store.SaveProduct(null, productName, price, "1", "5");
        return path;
    }

    // ------------------------------------------------------------------ parser

    [Fact]
    public void ParserRejectsUnexpectedEntriesAndDuplicates()
    {
        var source = BuildStandalone("صنف", "10");
        var archive = Path.Combine(root, "extra.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(source, ArchiveService.DatabaseEntry);
            using (var writer = zip.CreateEntry(ArchiveService.ManifestEntry).Open())
                JsonSerializer.Serialize(writer, new { product = ArchiveService.WindowsProduct, formatVersion = 1, schemaVersion = 8, appVersion = "t", createdAt = DateTimeOffset.UtcNow, checksums = new Dictionary<string, string> { [ArchiveService.DatabaseEntry] = ArchiveService.Hash(source) } });
            zip.CreateEntry("unexpected.txt");
        }
        Assert.Throws<InvalidDataException>(() => ArchiveService.Inspect(archive, ArchiveKind.WindowsBackup, Path.Combine(root, "stage1")));

        var duplicates = Path.Combine(root, "dup.zip");
        using (var zip = ZipFile.Open(duplicates, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(source, ArchiveService.DatabaseEntry);
            zip.CreateEntryFromFile(source, ArchiveService.DatabaseEntry);
        }
        Assert.Throws<InvalidDataException>(() => ArchiveService.Inspect(duplicates, ArchiveKind.WindowsBackup, Path.Combine(root, "stage2")));
    }

    [Fact]
    public void ParserRejectsWrongProductChecksumAndSchema()
    {
        var source = BuildStandalone("صنف", "10");
        var wrongProduct = Path.Combine(root, "wrong-product.zip");
        using (var zip = ZipFile.Open(wrongProduct, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(source, ArchiveService.DatabaseEntry);
            using var writer = zip.CreateEntry(ArchiveService.ManifestEntry).Open();
            JsonSerializer.Serialize(writer, new { product = ArchiveService.AndroidProduct, formatVersion = 1, schemaVersion = 8, appVersion = "t", createdAt = DateTimeOffset.UtcNow, checksums = new Dictionary<string, string> { [ArchiveService.DatabaseEntry] = ArchiveService.Hash(source) } });
        }
        Assert.Throws<InvalidDataException>(() => ArchiveService.Inspect(wrongProduct, ArchiveKind.WindowsBackup, Path.Combine(root, "stage3")));

        var badChecksum = Path.Combine(root, "bad-checksum.zip");
        using (var zip = ZipFile.Open(badChecksum, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(source, ArchiveService.DatabaseEntry);
            using var writer = zip.CreateEntry(ArchiveService.ManifestEntry).Open();
            JsonSerializer.Serialize(writer, new { product = ArchiveService.WindowsProduct, formatVersion = 1, schemaVersion = 8, appVersion = "t", createdAt = DateTimeOffset.UtcNow, checksums = new Dictionary<string, string> { [ArchiveService.DatabaseEntry] = new string('0', 64) } });
        }
        Assert.Throws<InvalidDataException>(() => ArchiveService.Inspect(badChecksum, ArchiveKind.WindowsBackup, Path.Combine(root, "stage4")));

        var badSchema = Path.Combine(root, "bad-schema.zip");
        using (var zip = ZipFile.Open(badSchema, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(source, ArchiveService.DatabaseEntry);
            using var writer = zip.CreateEntry(ArchiveService.ManifestEntry).Open();
            JsonSerializer.Serialize(writer, new { product = ArchiveService.WindowsProduct, formatVersion = 1, schemaVersion = 7, appVersion = "t", createdAt = DateTimeOffset.UtcNow, checksums = new Dictionary<string, string> { [ArchiveService.DatabaseEntry] = ArchiveService.Hash(source) } });
        }
        Assert.Throws<InvalidDataException>(() => ArchiveService.Inspect(badSchema, ArchiveKind.WindowsBackup, Path.Combine(root, "stage5")));
    }

    [Fact]
    public void InspectionReportsCountsAndCurrency()
    {
        SeedLive();
        var archive = BuildArchive(ArchiveKind.WindowsBackup, livePath, "windows-backup.zip");
        var inspection = ArchiveService.Inspect(archive, ArchiveKind.WindowsBackup, Path.Combine(root, "stage6"));
        Assert.Equal(1, inspection.Products);
        Assert.Equal(1, inspection.Invoices);
        Assert.Equal("EGP", inspection.Currency);
        Assert.Equal(2, inspection.MoneyMinorUnits);
        Assert.Equal(20000, inspection.Sales);
        Assert.Equal(5000, inspection.Paid);
        Assert.Equal(15000, inspection.Outstanding);
        Assert.True(File.Exists(inspection.StagedDatabase));
    }

    // ------------------------------------------------------------------ replace

    [Fact]
    public void ApplyReplacesEveryTableInBothDirections()
    {
        SeedLive();
        var store = Store.Open(livePath);
        store.SaveProduct(null, "صنف قديم فقط", "10", "1", "1");

        var stagedSource = BuildStandalone("صنف جديد", "250");
        var archive = BuildArchive(ArchiveKind.WindowsBackup, stagedSource, "replace.zip");
        var inspection = ArchiveService.Inspect(archive, ArchiveKind.WindowsBackup, Path.Combine(root, "stage7"));
        var report = ArchiveService.Apply(live, inspection, Path.Combine(root, "safety"));

        Assert.True(report.Replaced);
        Assert.True(File.Exists(report.SafetyArchive));
        var reopened = Store.Open(livePath);
        var products = reopened.Products("", false, 0);
        // The staged-only row is present and the old-only rows are gone.
        Assert.Single(products);
        Assert.Equal("صنف جديد", products[0].Name);
        Assert.Equal(25000, products[0].Price);
        Assert.Empty(reopened.Invoices("", null, 0));
        // The dataset identity is replaced by the source dataset.
        Assert.Empty(ArchiveService.Compare(inspection.StagedDatabase, livePath));
    }

    [Fact]
    public void ApplyDetectsSameCountDifferentIdentifiers()
    {
        var left = BuildStandalone("أ", "10");
        var right = BuildStandalone("ب", "10");
        Assert.NotEmpty(ArchiveService.Compare(left, right));
    }

    [Fact]
    public void ApplyHandlesCommittedWalOnTheLiveDatabase()
    {
        SeedLive();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = livePath, Pooling = false, ForeignKeys = true }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO products(id,name,name_key,price,threshold,created_at,updated_at) VALUES ('wal-1','صنف وول','صنف وول',10,1,'2026-01-01','2026-01-01')";
            command.ExecuteNonQuery();
        }
        var stagedSource = BuildStandalone("صنف مستبدل", "33");
        var archive = BuildArchive(ArchiveKind.WindowsBackup, stagedSource, "wal.zip");
        var inspection = ArchiveService.Inspect(archive, ArchiveKind.WindowsBackup, Path.Combine(root, "stage8"));
        ArchiveService.Apply(live, inspection, Path.Combine(root, "safety2"));

        var reopened = Store.Open(livePath);
        var products = reopened.Products("", false, 0);
        Assert.Single(products);
        Assert.Equal("صنف مستبدل", products[0].Name);
        Assert.Empty(ArchiveService.Compare(inspection.StagedDatabase, livePath));
    }

    [Fact]
    public void SafetyArchiveIsValidAndKeepsTheOldData()
    {
        SeedLive();
        var stagedSource = BuildStandalone("صنف مستبدل", "33");
        var archive = BuildArchive(ArchiveKind.WindowsBackup, stagedSource, "safety.zip");
        var inspection = ArchiveService.Inspect(archive, ArchiveKind.WindowsBackup, Path.Combine(root, "stage9"));
        var report = ArchiveService.Apply(live, inspection, Path.Combine(root, "safety3"));

        var safetyInspection = ArchiveService.Inspect(report.SafetyArchive, ArchiveKind.WindowsBackup, Path.Combine(root, "stage10"));
        Assert.Equal(1, safetyInspection.Products);
        Assert.Equal("صنف المصدر", safetyInspection.StagedDatabase is null ? "" : "صنف المصدر");
        var safetyStore = Store.Open(safetyInspection.StagedDatabase!);
        Assert.Equal("صنف المصدر", safetyStore.Products("", false, 0).Single().Name);
    }

    [Fact]
    public void FailedReplacementLeavesTheLiveDataIntact()
    {
        SeedLive();
        var before = Store.Open(livePath).Products("", false, 0).Single().Name;
        var stagedSource = BuildStandalone("صنف مستبدل", "33");
        var archive = BuildArchive(ArchiveKind.WindowsBackup, stagedSource, "broken.zip");

        // Break the staged database after the inspection so the copy cannot succeed.
        var inspection = ArchiveService.Inspect(archive, ArchiveKind.WindowsBackup, Path.Combine(root, "stage11"));
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = inspection.StagedDatabase, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE invoice_items";
            command.ExecuteNonQuery();
        }

        Assert.ThrowsAny<Exception>(() => ArchiveService.Apply(live, inspection, Path.Combine(root, "safety4")));
        var after = Store.Open(livePath).Products("", false, 0).Single().Name;
        Assert.Equal(before, after);
    }

    // ------------------------------------------------------------------ backups

    [Fact]
    public void ManualBackupHonoursTheChosenFileNameAndIsVerified()
    {
        SeedLive();
        var destination = Path.Combine(root, "اختياري-المستخدم.raadbackup");
        var result = BackupService.CreateManual(live, destination);
        Assert.Equal(Path.GetFullPath(destination), Path.GetFullPath(result.Path));
        Assert.True(result.Verified);
        Assert.True(File.Exists(destination));
        var inspection = BackupService.Inspect(destination);
        Assert.Equal(1, inspection.Products);
    }

    [Fact]
    public void DailyBackupRunsOncePerDayAndRetriesAfterAFailure()
    {
        SeedLive();
        var directory = Path.Combine(root, "auto");
        var first = BackupService.EnsureDaily(live, directory);
        Assert.NotNull(first);
        Assert.Null(BackupService.EnsureDaily(live, directory)); // same day: no second run

        // A failed attempt must be retried and must not claim a success.
        var stateFile = Path.Combine(directory, "daily-backup-state.json");
        File.WriteAllText(stateFile, "{\"LastAttemptDay\":\"2020-01-01\",\"LastSuccessDay\":\"2020-01-01\",\"LastFailure\":\"boom\"}");
        var retry = BackupService.EnsureDaily(live, directory);
        Assert.NotNull(retry);
        Assert.NotNull(BackupService.LastDailySuccess(directory));
    }

    [Fact]
    public void AndroidArchiveUsesTheSameParserAndReplacement()
    {
        var stagedSource = BuildStandalone("صنف أندرويد", "70");
        var archive = BuildArchive(ArchiveKind.AndroidTransfer, stagedSource, "android.zip");
        var inspection = ArchiveService.Inspect(archive, ArchiveKind.AndroidTransfer, Path.Combine(root, "stage12"));
        Assert.Equal(1, inspection.Products);
        ArchiveService.Apply(live, inspection, Path.Combine(root, "safety5"));
        var reopened = Store.Open(livePath);
        Assert.Equal("صنف أندرويد", reopened.Products("", false, 0).Single().Name);
    }

    [Fact]
    public void SaveAndIssueIsOneGuardedOperation()
    {
        var store = Store.Open(livePath);
        store.SaveProduct(null, "صنف", "100", "1", "5");
        var product = store.Products("", false, 0).Single();
        var issued = store.SaveAndIssue(null, "100", new[] { new DraftLine(product.Id, product.Name, "1", "100") });
        Assert.StartsWith("INV-", issued.Reference);
        Assert.Equal(10000, issued.Total);
        Assert.Null(store.LoadDraft());
        Assert.Single(store.Invoices("", null, 0));

        // A failing save must not issue anything.
        var other = store.Products("", false, 0).Single();
        Assert.ThrowsAny<Exception>(() => store.SaveAndIssue(null, "999999999999999",
            new[] { new DraftLine(other.Id, other.Name, "1", "100") }));
        Assert.Single(store.Invoices("", null, 0));
    }
}
