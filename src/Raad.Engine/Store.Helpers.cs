using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Raad.Engine;

public sealed partial class Store
{
    private static string NewId() => Id();

    internal static string Stamp(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static Customer ReadCustomer(SqliteConnection connection, SqliteTransaction? tx, string id) =>
        Rows(connection, tx, "SELECT id,reference,name,COALESCE(phone,''),0 FROM customers WHERE id=$a",
            r => new Customer(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), 0), id).SingleOrDefault()
        ?? throw new InvalidOperationException("العميل غير موجود.");
}
