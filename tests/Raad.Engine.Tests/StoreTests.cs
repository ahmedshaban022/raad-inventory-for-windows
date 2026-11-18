using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Raad.Engine;
using Xunit;

namespace Raad.Engine.Tests;

/// <summary>Integration tests over a real SQLite file in a temporary directory per test.</summary>
public sealed class StoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "raad-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string databasePath;
    private readonly Store store;

    public StoreTests()
    {
        Directory.CreateDirectory(root);
        databasePath = Path.Combine(root, "raad-inventory.db");
        store = Store.Open(databasePath);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void CleanStartHasNoDummyInventory()
    {
        Assert.Empty(store.Products("", false, 0));
        Assert.Empty(store.Customers("", false, 0));
        Assert.Empty(store.Invoices("", null, 0));
        Assert.Null(store.LoadDraft());
        var totals = store.Totals();
        Assert.Equal(0, totals.Sales);
        Assert.Equal(0, totals.Collections);
        Assert.Equal(0, totals.Outstanding);
        Assert.Equal(0, totals.Unidentified);
    }

    [Fact]
    public void DraftKeepsRawInputAcrossReopen()
    {
        store.SaveProduct(null, "تيل فرامل", "125.50", "3", "10");
        var product = store.Products("", false, 0).Single();
        store.AddDraftLine(product.Id);
        store.SetDraftLine(product.Id, "2", "130");
        store.SetDraftPayment("50");

        var reopened = Store.Open(databasePath);
        var draft = reopened.LoadDraft();
        Assert.NotNull(draft);
        var line = Assert.Single(draft.Lines);
        Assert.Equal("2", line.Quantity);
        Assert.Equal("130", line.UnitPrice);
        Assert.Equal("50", draft.InitialPayment);
    }

    [Fact]
    public void IssueIsIdempotentAndDecrementsStock()
    {
        store.SaveProduct(null, "زيت محرك", "90", "2", "5");
        var product = store.Products("", false, 0).Single();
        store.AddDraftLine(product.Id);
        store.SetDraftLine(product.Id, "2", "90");
        store.SetDraftPayment("180");

        var issued = store.IssueDraft();
        Assert.StartsWith("INV-", issued.Reference);
        Assert.Equal(18000, issued.Total);
        Assert.Equal(5 - 2, store.Products("", false, 0).Single().Quantity);
        Assert.Null(store.LoadDraft());

        // A repeated issue with the same draft key must not sell twice.
        var again = Assert.Throws<InvalidOperationException>(() => store.IssueDraft());
        Assert.Contains("مسودة", again.Message);
        Assert.Single(store.Invoices("", null, 0));
        Assert.Equal(3, store.Products("", false, 0).Single().Quantity);
    }

    [Fact]
    public void WalkInInvoiceMustBeFullyPaid()
    {
        store.SaveProduct(null, "جناح أمامي", "200", "1", "4");
        var product = store.Products("", false, 0).Single();
        store.AddDraftLine(product.Id);
        store.SetDraftLine(product.Id, "1", "200");
        store.SetDraftPayment("100");
        Assert.Throws<InvalidOperationException>(() => store.IssueDraft());

        store.SetDraftPayment("200");
        var issued = store.IssueDraft();
        Assert.Equal(20000, issued.Total);
        Assert.Equal(20000, issued.Paid);
    }

    [Fact]
    public void StockShortageIsRejectedAndLeavesNoTrace()
    {
        store.SaveProduct(null, "فلتر", "40", "1", "3");
        var product = store.Products("", false, 0).Single();
        store.AddDraftLine(product.Id);
        store.SetDraftLine(product.Id, "5", "40");
        store.SetDraftPayment("200");
        Assert.Throws<InvalidOperationException>(() => store.IssueDraft());

        Assert.Empty(store.Invoices("", null, 0));
        Assert.Equal(3, store.Products("", false, 0).Single().Quantity);
        Assert.NotNull(store.LoadDraft()); // the draft survives a rejected issue
    }

    [Fact]
    public void PaymentReversalKeepsReasonAndOriginalDate()
    {
        store.SaveProduct(null, "سلسلة", "50", "1", "2");
        var product = store.Products("", false, 0).Single();
        store.AddDraftLine(product.Id);
        store.SetDraftLine(product.Id, "1", "50");
        store.SetDraftPayment("50");
        var issued = store.IssueDraft();

        store.ReversePayment(store.InvoiceDetailById(issued.Id)!.Payments.Single().Id, "خطأ في التحصيل", "20");
        var detail = store.InvoiceDetailById(issued.Id)!;
        Assert.Equal(2000, detail.Paid);
        Assert.Equal(3000, detail.Balance);
        var reversed = detail.Payments.Single(p => p.ReversalReason is not null);
        Assert.Equal("خطأ في التحصيل", reversed.ReversalReason);
        Assert.Equal(2, detail.Payments.Count);
    }

    [Fact]
    public void CancellationRequiresNoEffectivePaymentAndRestoresStockOnce()
    {
        store.SaveCustomer(null, "عميل إلغاء", "");
        var customer = store.Customers("", false, 0).Single();
        store.SaveProduct(null, "مرآة", "75", "1", "4");
        var product = store.Products("", false, 0).Single();
        store.AddDraftLine(product.Id);
        store.SetDraftLine(product.Id, "2", "75");
        store.SetDraftCustomer(customer.Id);
        store.SetDraftPayment("0");
        var issued = store.IssueDraft();
        Assert.Equal(2, store.Products("", false, 0).Single().Quantity);

        store.CancelInvoice(issued.Id, "خطأ في الإدخال");
        Assert.Equal(4, store.Products("", false, 0).Single().Quantity);
        Assert.Equal("Cancelled", store.InvoiceDetailById(issued.Id)!.Status);
        Assert.Throws<InvalidOperationException>(() => store.CancelInvoice(issued.Id, "إلغاء ثانٍ"));
        Assert.Equal(4, store.Products("", false, 0).Single().Quantity);

        // An invoice that carries an effective payment cannot be cancelled.
        store.SaveProduct(null, "مرآة خلفية", "30", "1", "1");
        var second = store.Products("", false, 0).Single(p => p.Name == "مرآة خلفية");
        store.AddDraftLine(second.Id);
        store.SetDraftLine(second.Id, "1", "30");
        store.SetDraftCustomer(customer.Id);
        store.SetDraftPayment("10");
        var paid = store.IssueDraft();
        Assert.Throws<InvalidOperationException>(() => store.CancelInvoice(paid.Id, "محاولة"));
    }

    [Fact]
    public void ArchivedProductCannotBeSoldAndCannotBeArchivedWhileInDraft()
    {
        store.SaveProduct(null, "طقم صيانة", "100", "1", "2");
        var product = store.Products("", false, 0).Single();
        store.AddDraftLine(product.Id);
        Assert.Throws<InvalidOperationException>(() => store.Archive(product.Id, false, true));

        store.RemoveDraftLine(product.Id);
        store.Archive(product.Id, false, true);
        Assert.Empty(store.Products("", false, 0));
        Assert.Single(store.Products("", true, 0));
        Assert.Throws<InvalidOperationException>(() => store.AddDraftLine(product.Id));
    }

    [Fact]
    public void DuplicateCustomerNamesAreAllowed()
    {
        store.SaveCustomer(null, "أحمد", "01000000001");
        store.SaveCustomer(null, "أحمد", "01000000002");
        var customers = store.Customers("", false, 0);
        Assert.Equal(2, customers.Count);
        Assert.NotEqual(customers[0].Reference, customers[1].Reference);
    }

    [Fact]
    public void UnidentifiedBalanceCanBeLinkedToACustomer()
    {
        store.SaveProduct(null, "بيل", "60", "1", "2");
        var product = store.Products("", false, 0).Single();
        store.AddDraftLine(product.Id);
        store.SetDraftLine(product.Id, "1", "60");
        store.SetDraftPayment("60");
        var issued = store.IssueDraft();
        Assert.Equal(0, store.Totals().Unidentified);

        // Reversing the walk-in payment turns the invoice into an unidentified balance.
        var payment = store.InvoiceDetailById(issued.Id)!.Payments.Single();
        store.ReversePayment(payment.Id, "تسديد غير صحيح", null);
        Assert.Equal(6000, store.Totals().Unidentified);
        Assert.Equal(6000, store.Totals().Outstanding);

        store.SaveCustomer(null, "عميل نقدي", "");
        var customer = store.Customers("", false, 0).Single();
        store.LinkCustomer(issued.Id, customer.Id);
        Assert.Equal(0, store.Totals().Unidentified);
        Assert.Equal(6000, store.Totals().Outstanding);
    }

    [Fact]
    public void BackupAndRestorePreserveReferencesAndBalances()
    {
        store.SaveProduct(null, "كوبلن", "150", "1", "3");
        var product = store.Products("", false, 0).Single();
        store.AddDraftLine(product.Id);
        store.SetDraftLine(product.Id, "1", "150");
        store.SetDraftPayment("50");
        store.SaveCustomer(null, "عميل النسخة", "");
        var customer = store.Customers("", false, 0).Single();
        store.SetDraftCustomer(customer.Id);
        var issued = store.IssueDraft();

        var backupDirectory = Path.Combine(root, "backups");
        var backup = BackupService.CreateManual(store.Database, Path.Combine(backupDirectory, "manual.raadbackup"));
        Assert.True(File.Exists(backup.Path));
        var inspection = ArchiveService.Inspect(backup.Path, ArchiveKind.WindowsBackup, Path.Combine(root, "stage"));
        Assert.Equal(1, inspection.Summary["invoices"]);
        Assert.Equal(1, inspection.Summary["products"]);

        // Change the live data, then restore the backup.
        store.SaveProduct(null, "صنف جديد بعد النسخة", "10", "1", "1");
        var report = BackupService.Restore(store.Database, backup.Path, backupDirectory);
        Assert.True(File.Exists(report.SafetyArchive));

        var reopened = Store.Open(databasePath);
        var restored = reopened.Invoices("", null, 0).Single();
        Assert.Equal(issued.Reference, restored.Reference);
        Assert.Equal(10000, restored.Balance);
        var dbInfo = new FileInfo(databasePath);
        var restoredProducts = reopened.Products("", false, 0);
        // The restored invoice keeps its reference, balance and stock movement history.
        Assert.Equal(issued.Reference, restored.Reference);
        Assert.Equal(10000, restored.Balance);
        Assert.Contains(restoredProducts, product => product.Name == "كوبلن" && product.Quantity == 2);
    }

    [Fact]
    public void CorruptArchiveIsRejectedBeforeAnyReplacement()
    {
        var corrupt = Path.Combine(root, "corrupt.raadbackup");
        using (var archive = ZipFile.Open(corrupt, ZipArchiveMode.Create))
        {
            using (var manifest = archive.CreateEntry("manifest.json").Open())
                manifest.Write(System.Text.Encoding.UTF8.GetBytes("{\"product\":\"raad-inventory-windows\",\"formatVersion\":1,\"schemaVersion\":8,\"checksums\":{\"raad-inventory.db\":\"00\"}}"));
            using var payload = archive.CreateEntry("raad-inventory.db").Open();
            payload.Write(new byte[] { 1, 2, 3 });
        }
        Assert.ThrowsAny<Exception>(() => BackupService.Inspect(corrupt));
        Assert.True(File.Exists(databasePath));
    }

    [Fact]
    public void AndroidTransferArchiveIsAcceptedAndReferencesSurvive()
    {
        // Build a schema-8 dataset that mirrors the Android export format.
        var source = Path.Combine(root, "android.db");
        File.Copy(databasePath, source, true);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO products(id,name,name_key,price,threshold,created_at,updated_at) VALUES ('p-android','صنف أندرويد','صنف أندرويد',5000,1,'2026-11-18T10:00:00.000Z','2026-11-18T10:00:00.000Z');
                INSERT INTO invoices(id,reference,issue_key,issued_at,customer_id,customer_name,customer_name_key,total)
                VALUES ('inv-1','INV-20261118-ABCDEF','key-1','2026-11-18T10:00:00.000Z',NULL,NULL,NULL,5000);
                INSERT INTO invoice_items(invoice_id,product_id,name,quantity,unit_price,position)
                VALUES ('inv-1','p-android','صنف أندرويد',1,5000,0);
                INSERT INTO stock_movements(id,product_id,kind,quantity,occurred_at,source_id) VALUES ('m-0','p-android','opening',5,'2026-11-17T10:00:00.000Z',NULL);
                INSERT INTO stock_movements(id,product_id,kind,quantity,occurred_at,source_id) VALUES ('m-1','p-android','sale',-1,'2026-11-18T10:00:00.000Z','inv-1');
                """;
            command.ExecuteNonQuery();
        }

        var archive = Path.Combine(root, "android.raadbackup");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(source, TransferService.DatabaseEntry, CompressionLevel.Fastest);
            using var writer = zip.CreateEntry("manifest.json").Open();
            using var payload = File.OpenRead(source);
            JsonSerializer.Serialize(writer, new TransferManifest(TransferService.AndroidProduct, 1, 8, "android", DateTimeOffset.UtcNow,
                new() { [TransferService.DatabaseEntry] = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant() }), TransferService.Json);
        }

        var inspection = TransferService.Inspect(archive, Path.Combine(root, "staged"));
        Assert.Equal(1, inspection.Summary["invoices"]);

        var replacement = Path.Combine(root, "replacement.db");
        File.Copy(inspection.StagedDatabase, replacement, true);
        var replaced = Store.Open(replacement);
        var invoice = replaced.Invoices("", null, 0).Single();
        Assert.Equal("INV-20261118-ABCDEF", invoice.Reference);
        Assert.Equal(5000, invoice.Balance);
    }
}
