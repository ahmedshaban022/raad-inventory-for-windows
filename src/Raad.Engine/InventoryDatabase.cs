using Microsoft.Data.Sqlite;

namespace Raad.Engine;

public sealed class InventoryDatabase(string path)
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    public SqliteConnection Open(bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false, ForeignKeys = true }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }
    public IReadOnlyList<Product> Search(string query, int offset = 0)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        using var connection = Open(true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,price,quantity,threshold FROM product_stock WHERE archived_at IS NULL AND instr(name_key,$query)>0 ORDER BY name_key LIMIT 30 OFFSET $offset";
        command.Parameters.AddWithValue("$query", ExactInput.Normalize(query));
        command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader();
        var rows = new List<Product>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)));
        return rows;
    }
    public InvoiceSnapshot FirstInvoice()
    {
        using var connection = Open(true);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT i.id,i.reference,COALESCE(i.customer_name,'من غير اسم عميل'),i.issued_at,i.total,i.cancelled_at,
              COALESCE((SELECT SUM(p.amount) FROM payments p WHERE p.invoice_id=i.id AND NOT EXISTS
                (SELECT 1 FROM payment_reversals r WHERE r.payment_id=p.id)),0)
            FROM invoices i ORDER BY i.issued_at DESC,i.id DESC LIMIT 1
            """;
        string id, reference, customer, issued; long total, paid; bool cancelled;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("لا توجد فاتورة في بيانات الاختبار.");
            id = reader.GetString(0); reference = reader.GetString(1); customer = reader.GetString(2);
            issued = reader.GetString(3); total = reader.GetInt64(4); cancelled = !reader.IsDBNull(5); paid = reader.GetInt64(6);
        }
        command.CommandText = "SELECT product_id,name,quantity,unit_price FROM invoice_items WHERE invoice_id=$id ORDER BY position";
        command.Parameters.AddWithValue("$id", id);
        var lines = new List<InvoiceLine>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) lines.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3)));
        transaction.Commit();
        return new(reference, customer, DateTimeOffset.Parse(issued), DateTimeOffset.UtcNow,
            cancelled ? "Cancelled" : paid == total ? "Paid" : paid == 0 ? "Unpaid" : "Partially paid", total, paid, lines);
    }
    public static Dictionary<string, long> Reconcile(SqliteConnection connection)
    {
        var expressions = new Dictionary<string, string>
        {
            ["products"] = "SELECT COUNT(*) FROM products", ["customers"] = "SELECT COUNT(*) FROM customers",
            ["invoices"] = "SELECT COUNT(*) FROM invoices", ["items"] = "SELECT COUNT(*) FROM invoice_items",
            ["payments"] = "SELECT COUNT(*) FROM payments", ["reversals"] = "SELECT COUNT(*) FROM payment_reversals",
            ["movements"] = "SELECT COUNT(*) FROM stock_movements", ["drafts"] = "SELECT COUNT(*) FROM invoice_draft",
            ["stock"] = "SELECT COALESCE(SUM(quantity),0) FROM stock_movements",
            ["sales"] = "SELECT COALESCE(SUM(total),0) FROM invoices WHERE cancelled_at IS NULL",
            ["paid"] = "SELECT COALESCE(SUM(p.amount),0) FROM payments p WHERE NOT EXISTS (SELECT 1 FROM payment_reversals r WHERE r.payment_id=p.id)",
            ["outstanding"] = "SELECT COALESCE(SUM(i.total-COALESCE((SELECT SUM(p.amount) FROM payments p WHERE p.invoice_id=i.id AND NOT EXISTS (SELECT 1 FROM payment_reversals r WHERE r.payment_id=p.id)),0)),0) FROM invoices i WHERE cancelled_at IS NULL",
            ["legacyDeletionTraces"] = "SELECT COUNT(*) FROM stock_movements WHERE reason='فاتورة محذوفة نهائيًا'"
        };
        var result = new Dictionary<string, long>();
        foreach (var (key, sql) in expressions)
        {
            using var command = connection.CreateCommand(); command.CommandText = sql;
            result[key] = Convert.ToInt64(command.ExecuteScalar());
        }
        return result;
    }
    public void Backup(string destination)
    {
        using var source = Open(true);
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        target.Open(); source.BackupDatabase(target);
        using var check = target.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
        if (!Equals(check.ExecuteScalar(), "ok")) throw new InvalidDataException("فشل فحص النسخة الاحتياطية.");
    }
}
