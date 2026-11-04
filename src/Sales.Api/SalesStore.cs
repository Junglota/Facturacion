using Integration.Contracts;
using Integration.Data;
using Microsoft.Data.Sqlite;
using static Integration.Data.SqliteDb;

namespace Sales.Api;

public record SaleOperation(Sale Sale, string OperationId, int? ErrorStatus);

public sealed class SalesStore
{
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS sales (
            id INTEGER PRIMARY KEY, product_id INTEGER NOT NULL, quantity INTEGER NOT NULL CHECK(quantity > 0),
            reference TEXT NOT NULL UNIQUE, total_cents INTEGER,
            status TEXT NOT NULL CHECK(status IN ('Pending','Confirming','Confirmed','Rejected','Cancelling','Cancelled')),
            operation_id TEXT NOT NULL UNIQUE, failure TEXT, error_status INTEGER);
        """;
    private readonly SqliteDb database;
    public SalesStore(string path) => database = new(path, Schema);
    private static SaleOperation Read(SqliteDataReader row) => new(
        new(row.GetInt64(0), row.GetInt64(1), row.GetInt32(2), row.GetString(3), row.IsDBNull(4) ? null : row.GetInt64(4),
            row.GetString(5), row.IsDBNull(7) ? null : row.GetString(7)), row.GetString(6), row.IsDBNull(8) ? null : row.GetInt32(8));
    public SaleOperation Start(SaleInput input)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        SaleOperation? previous;
        using (var query = Command(db, "SELECT * FROM sales WHERE reference=$ref", tx, ("$ref", input.Reference.Trim())))
        {
            using var row = query.ExecuteReader();
            previous = row.Read() ? Read(row) : null;
        }
        if (previous is not null)
        {
            if (previous.Sale.ProductId != input.ProductId || previous.Sale.Quantity != input.Quantity)
                throw new BusinessException(409, "La referencia ya existe con otros datos");
            return previous;
        }
        var operation = Guid.NewGuid().ToString("N");
        using var insert = Command(db, "INSERT INTO sales(product_id,quantity,reference,status,operation_id) VALUES($id,$qty,$ref,'Pending',$op); SELECT last_insert_rowid();", tx,
            ("$id", input.ProductId), ("$qty", input.Quantity), ("$ref", input.Reference.Trim()), ("$op", operation));
        var id = (long)insert.ExecuteScalar()!;
        tx.Commit();
        return new(new(id, input.ProductId, input.Quantity, input.Reference.Trim(), null, "Pending"), operation, null);
    }
    public SaleOperation? Get(long id)
    {
        using var db = database.Open();
        using var query = Command(db, "SELECT * FROM sales WHERE id=$id", null, ("$id", id));
        using var row = query.ExecuteReader();
        return row.Read() ? Read(row) : null;
    }
    public List<Sale> List(int offset, int limit)
    {
        using var db = database.Open();
        using var query = Command(db, "SELECT * FROM sales ORDER BY id LIMIT $limit OFFSET $offset", null, ("$limit", limit), ("$offset", offset));
        using var rows = query.ExecuteReader();
        var result = new List<Sale>();
        while (rows.Read()) result.Add(Read(rows).Sale);
        return result;
    }
    public List<long> Pending()
    {
        using var db = database.Open();
        using var query = Command(db, "SELECT id FROM sales WHERE status IN ('Pending','Confirming','Cancelling') ORDER BY id");
        using var rows = query.ExecuteReader();
        var ids = new List<long>();
        while (rows.Read()) ids.Add(rows.GetInt64(0));
        return ids;
    }
    public void PrepareConfirmation(long id, long total)
    {
        using var db = database.Open();
        using var command = Command(db, "UPDATE sales SET status='Confirming',total_cents=$total WHERE id=$id AND status='Pending'", null, ("$total", total), ("$id", id));
        command.ExecuteNonQuery();
    }
    public void SetStatus(long id, string status, string expected, string? failure = null, int? errorStatus = null)
    {
        using var db = database.Open();
        using var command = Command(db, "UPDATE sales SET status=$status,failure=$failure,error_status=$error WHERE id=$id AND status=$expected", null,
            ("$status", status), ("$failure", failure), ("$error", errorStatus), ("$id", id), ("$expected", expected));
        command.ExecuteNonQuery();
    }
}
