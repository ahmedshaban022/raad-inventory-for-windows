using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Raad.Engine;
using Xunit;

namespace Raad.Engine.Tests;

public sealed class TransferTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "RaadFeasibilityTests", Guid.NewGuid().ToString("N"));
    private readonly string fixture;
    public TransferTests()
    {
        Directory.CreateDirectory(temporary);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        fixture = Path.Combine(root?.FullName ?? throw new InvalidOperationException("Workspace not found"), "artifacts", "fixtures", "android-v8.db");
        if (!File.Exists(fixture)) throw new InvalidOperationException("Run scripts/create-android-fixture.mjs before tests.");
    }
    [Fact]
    public void InspectPreservesExactSourceHistoryAndNeverChangesSource()
    {
        var before = SHA256.HashData(File.ReadAllBytes(fixture));
        var archive = Path.Combine(temporary, "transfer.raadbackup");
        TransferService.WriteArchive(fixture, archive);
        var inspected = TransferService.Inspect(archive, Path.Combine(temporary, "staged"));
        Assert.Equal(12, inspected.Summary["products"]);
        Assert.Equal(2, inspected.Summary["invoices"]);
        Assert.Equal(4, inspected.Summary["legacyDeletionTraces"]);
        Assert.Equal(1, inspected.Summary["drafts"]);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(fixture)));
        using var expected = new InventoryDatabase(fixture).Open(true);
        using var actual = new InventoryDatabase(inspected.StagedDatabase).Open(true);
        Assert.Equal(InventoryDatabase.Reconcile(expected).OrderBy(x => x.Key), InventoryDatabase.Reconcile(actual).OrderBy(x => x.Key));
        using var zip = ZipFile.OpenRead(archive);
        Assert.Equal(new[] { "manifest.json", "raad-inventory.db" }, zip.Entries.Select(x => x.FullName).Order());
    }
    [Fact]
    public void BadChecksumIsRejectedAndPrivateStagingIsCleaned()
    {
        var archive = Path.Combine(temporary, "bad.raadbackup"); TransferService.WriteArchive(fixture, archive);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("manifest.json")!; TransferManifest manifest;
            using (var input = entry.Open()) manifest = JsonSerializer.Deserialize<TransferManifest>(input, TransferService.Json)!;
            entry.Delete(); manifest.Checksums[TransferService.DatabaseEntry] = new string('0', 64);
            using var output = zip.CreateEntry("manifest.json").Open(); JsonSerializer.Serialize(output, manifest, TransferService.Json);
        }
        var staging = Path.Combine(temporary, "staged");
        Assert.Throws<InvalidDataException>(() => TransferService.Inspect(archive, staging));
        Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
    }
    [Theory]
    [InlineData("../outside.db")]
    [InlineData("manifest.json")]
    [InlineData("installation/password.txt")]
    public void UnsafeOrDuplicateEntriesAreRejectedBeforeExtraction(string entry)
    {
        var archive = Path.Combine(temporary, "unsafe.zip"); TransferService.WriteArchive(fixture, archive);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update)) using (var stream = zip.CreateEntry(entry).Open()) stream.WriteByte(1);
        Assert.Throws<InvalidDataException>(() => TransferService.Inspect(archive, Path.Combine(temporary, "staged")));
    }
    [Fact]
    public void UnknownSchemaIsRejectedEvenWithCorrectChecksum()
    {
        var modified = Path.Combine(temporary, "future.db"); File.Copy(fixture, modified);
        using (var connection = new InventoryDatabase(modified).Open())
        { using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version=9"; command.ExecuteNonQuery(); }
        var archive = Path.Combine(temporary, "future.zip"); TransferService.WriteArchive(modified, archive);
        Assert.Throws<InvalidDataException>(() => TransferService.Inspect(archive, Path.Combine(temporary, "staged")));
    }
    [Fact]
    public void InconsistentInvoiceTotalsAreRejected()
    {
        var modified = Path.Combine(temporary, "wrong.db"); File.Copy(fixture, modified);
        using (var connection = new InventoryDatabase(modified).Open())
        { using var command = connection.CreateCommand(); command.CommandText = "UPDATE invoices SET total=total+1"; command.ExecuteNonQuery(); }
        var archive = Path.Combine(temporary, "wrong.zip"); TransferService.WriteArchive(modified, archive);
        Assert.Throws<InvalidDataException>(() => TransferService.Inspect(archive, Path.Combine(temporary, "staged")));
    }
    [Fact]
    public void OnlineBackupIncludesCommittedWalAndHasMatchingBalances()
    {
        var live = Path.Combine(temporary, "live.db"); File.Copy(fixture, live);
        using var writer = new InventoryDatabase(live).Open();
        using var command = writer.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; INSERT INTO stock_movements(id,product_id,kind,quantity,occurred_at) SELECT 'backup-proof',id,'addition',1,'2026-11-18T12:00:00Z' FROM products LIMIT 1";
        command.ExecuteNonQuery();
        var output = Path.Combine(temporary, "backup.db"); new InventoryDatabase(live).Backup(output);
        using var backup = new InventoryDatabase(output).Open(true);
        Assert.Equal(InventoryDatabase.Reconcile(writer).OrderBy(x => x.Key), InventoryDatabase.Reconcile(backup).OrderBy(x => x.Key));
    }
    public void Dispose() => Directory.Delete(temporary, true);
}

public class ExactInputTests
{
    [Theory]
    [InlineData("١٢٥٫٠٥", 12505)]
    [InlineData("۱۲۵.۰۵", 12505)]
    [InlineData(".05", 5)]
    [InlineData("0", 0)]
    [InlineData("9999999999.99", ExactInput.MaxMoney)]
    public void MoneyMatchesRaadParser(string input, long expected) => Assert.Equal(expected, ExactInput.Money(input));
    [Theory]
    [InlineData("1,25")]
    [InlineData("1.005")]
    [InlineData("-1")]
    [InlineData("10000000000")]
    public void InvalidMoneyIsNotRoundedOrCoerced(string input) => Assert.Throws<ArgumentException>(() => ExactInput.Money(input));
    [Theory]
    [InlineData("٠٢", 2)]
    [InlineData("۲۱۴۷۴۸۳۶۴۷", ExactInput.MaxQuantity)]
    public void WholeUnitsAcceptDigitForms(string input, long expected) => Assert.Equal(expected, ExactInput.Quantity(input));
    [Theory]
    [InlineData("0.5")]
    [InlineData("2147483648")]
    [InlineData("-1")]
    public void InvalidQuantityIsRejected(string input) => Assert.Throws<ArgumentException>(() => ExactInput.Quantity(input));
}
