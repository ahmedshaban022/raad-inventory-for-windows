using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Raad.Engine;

public sealed partial class Store(string path)
{
    public InventoryDatabase Database { get; } = new(path);
    public const int PageSize = 30;

    public static Store Open(string path)
    {
        var store = new Store(path);
        if (!File.Exists(store.Database.Path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(store.Database.Path)!);
            var temporary = store.Database.Path + "." + Id();
            try
            {
                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = temporary, Pooling = false, ForeignKeys = true }.ToString()))
                {
                    connection.Open();
                    using var transaction = connection.BeginTransaction();
                    using var stream = typeof(Store).Assembly.GetManifestResourceStream("Raad.Engine.Schema.sql")!;
                    using var reader = new StreamReader(stream);
                    Execute(connection, transaction, reader.ReadToEnd());
                    Execute(connection, transaction, "INSERT INTO dataset VALUES (1, $a, 'EGP')", Id());
                    transaction.Commit();
                }
                File.Move(temporary, store.Database.Path);
            }
            finally { File.Delete(temporary); }
        }
        using var check = store.Database.Open();
        if (Convert.ToInt64(Scalar(check, null, "PRAGMA user_version")) != 8)
            throw new InvalidDataException("إصدار قاعدة بيانات رعد غير مدعوم.");
        return store;
    }

    public IReadOnlyList<Product> Products(string query, bool archived, int offset) => Read(connection =>
        Rows(connection, null, """
            SELECT id,name,price,quantity,threshold FROM product_stock
            WHERE (archived_at IS NOT NULL)=$a AND instr(name_key,$b)>0 ORDER BY name_key,id LIMIT 30 OFFSET $c
            """, r => new Product(r.GetString(0),r.GetString(1),r.GetInt64(2),r.GetInt64(3),r.GetInt64(4)), archived, ExactInput.Normalize(query), offset));

    public void SaveProduct(string? id, string name, string price, string threshold, string opening) => Write((connection, tx) =>
    {
        name = Required(name, "اكتب اسم الصنف.");
        var normalized = ExactInput.Normalize(name);
        if (Scalar(connection, tx, "SELECT id FROM products WHERE name_key=$a AND ($b IS NULL OR id<>$b)", normalized, id) is not null)
            throw new InvalidOperationException("يوجد صنف بنفس الاسم.");
        if (id is null)
        {
            id = Id();
            Execute(connection, tx, "INSERT INTO products(id,name,name_key,price,threshold,created_at,updated_at) VALUES ($a,$b,$c,$d,$e,$f,$f)",
                id, name, normalized, ExactInput.Money(price), ExactInput.Quantity(threshold), Time());
            var quantity = ExactInput.Quantity(opening);
            if (quantity > 0) MoveStock(connection, tx, id, "opening", quantity);
        }
        else Execute(connection, tx, "UPDATE products SET name=$b,name_key=$c,price=$d,threshold=$e,updated_at=$f WHERE id=$a",
            id, name, normalized, ExactInput.Money(price), ExactInput.Quantity(threshold), Time());
    });

    public void AdjustStock(string id, string input, string? reason) => Write((connection, tx) =>
    {
        var item = GetProduct(connection, tx, id, active: true);
        var change = reason is null ? ExactInput.Quantity(input, 1) : ExactInput.Quantity(input) - item.Quantity;
        if (reason is not null) reason = Required(reason, "اكتب سبب تصحيح المخزون.");
        CheckStock(item.Quantity + change);
        if (change != 0) MoveStock(connection, tx, id, reason is null ? "addition" : "correction", change, reason: reason);
    });

    public IReadOnlyList<Movement> Movements(string id, int offset) => Read(connection => Rows(connection, null,
        "SELECT kind,quantity,occurred_at,COALESCE(reason, (SELECT reference FROM invoices WHERE id = m.source_id), '') FROM stock_movements m WHERE product_id=$a ORDER BY occurred_at DESC,rowid DESC LIMIT 30 OFFSET $b",
        r => new Movement(r.GetString(0),r.GetInt64(1),ParseTime(r.GetString(2)),r.GetString(3)), id, offset));

    public IReadOnlyList<Customer> Customers(string filter, bool archived, int offset) => Read(connection =>
        Rows(connection, null, """
            SELECT c.id,c.reference,c.name,COALESCE(c.phone,''),COALESCE((
              SELECT SUM(i.balance) FROM invoice_balances i WHERE COALESCE(i.linked_customer_id,i.customer_id)=c.id AND i.balance>0),0)
            FROM (SELECT * FROM customers WHERE (archived_at IS NOT NULL)=$a AND
              (instr(name_key,$b)>0 OR instr(lower(reference),$b)>0 OR ($c<>'' AND instr(phone_key,$c)>0))
              ORDER BY name_key,id LIMIT 30 OFFSET $d) c ORDER BY c.name_key,c.id
            """, ReadCustomer, archived, ExactInput.Normalize(filter), PhoneKey(filter), offset));

    public void SaveCustomer(string? id, string name, string phone) => Write((connection, tx) =>
    {
        name = Required(name, "اكتب اسم العميل.");
        var phoneKey = PhoneKey(phone);
        if (!string.IsNullOrWhiteSpace(phone) && phoneKey.Length == 0) throw new ArgumentException("اكتب رقم هاتف صالحًا.");
        if (id is null)
        {
            var number = Convert.ToInt64(Scalar(connection, tx, "SELECT COALESCE(MAX(reference_number),0)+1 FROM customers"));
            Execute(connection, tx, """
                INSERT INTO customers(reference_number,id,reference,name,name_key,phone,phone_key,created_at)
                VALUES ($a,$b,$c,$d,$e,$f,$g,$h)
                """, number, Id(), "C-" + number.ToString("D4", CultureInfo.InvariantCulture), name, ExactInput.Normalize(name), Optional(phone), phoneKey, Time());
        }
        else Execute(connection, tx, "UPDATE customers SET name=$b,name_key=$c,phone=$d,phone_key=$e WHERE id=$a",
            id, name, ExactInput.Normalize(name), Optional(phone), phoneKey);
    });

    public void Archive(string id, bool customer, bool archived) => Write((connection, tx) =>
    {
        var draftSql = customer ? "SELECT 1 FROM invoice_draft WHERE customer_id=$a" : "SELECT 1 FROM draft_lines WHERE product_id=$a";
        if (archived && Scalar(connection, tx, draftSql, id) is not null)
            throw new InvalidOperationException("العنصر موجود في مسودة البيع الحالية.");
        Execute(connection, tx, customer ? "UPDATE customers SET archived_at=$b WHERE id=$a" : "UPDATE products SET archived_at=$b WHERE id=$a",
            id, archived ? Time() : null);
    });

    private static Customer ReadCustomer(SqliteDataReader r) => new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt64(4));

    private static Product GetProduct(SqliteConnection connection, SqliteTransaction tx, string id, bool active) =>
        Rows(connection, tx, "SELECT id,name,price,quantity,threshold FROM product_stock WHERE id=$a AND ($b=0 OR archived_at IS NULL)",
            r => new Product(r.GetString(0),r.GetString(1),r.GetInt64(2),r.GetInt64(3),r.GetInt64(4)), id, active).SingleOrDefault()
        ?? throw new InvalidOperationException("الصنف غير متاح.");

    private static void MoveStock(SqliteConnection connection, SqliteTransaction tx, string id, string kind, long amount, string? source = null, string? reason = null) =>
        Execute(connection, tx, "INSERT INTO stock_movements(id,product_id,kind,quantity,occurred_at,source_id,reason) VALUES ($a,$b,$c,$d,$e,$f,$g)",
            Id(), id, kind, amount, Time(), source, reason);

    private static void CheckStock(long amount)
    {
        if (amount is < 0 or > ExactInput.MaxQuantity) throw new InvalidOperationException("الكمية تتجاوز المخزون المتاح أو حدود المخزون.");
    }

    private T Read<T>(Func<SqliteConnection,T> read)
    {
        using var connection = Database.Open(true);
        return read(connection);
    }

    private T Write<T>(Func<SqliteConnection,SqliteTransaction,T> action)
    {
        using var connection = Database.Open();
        using var transaction = connection.BeginTransaction();
        var value = action(connection, transaction);
        transaction.Commit();
        return value;
    }

    private void Write(Action<SqliteConnection,SqliteTransaction> action) =>
        Write((connection, transaction) => { action(connection,transaction); return true; });

    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql, object?[] args)
    {
        var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue("$" + (char)('a'+i), args[i] ?? DBNull.Value);
        return command;
    }

    private static int Execute(SqliteConnection c, SqliteTransaction? tx, string sql, params object?[] args)
    {
        using var command = Command(c,tx,sql,args);
        return command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, SqliteTransaction? tx, string sql, params object?[] args)
    {
        using var command = Command(c,tx,sql,args);
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private static List<T> Rows<T>(SqliteConnection c, SqliteTransaction? tx, string sql, Func<SqliteDataReader,T> convert, params object?[] args)
    {
        using var command = Command(c,tx,sql,args);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(convert(reader));
        return rows;
    }

    private static string Id() => Guid.NewGuid().ToString("N");
    private static string Time(DateTimeOffset? time = null) =>
        (time ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static string PhoneKey(string value) => string.Concat(ExactInput.Digits(value).Where(char.IsAsciiDigit));
    private static string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Required(string value, string error) => Optional(value) ?? throw new ArgumentException(error);
}
