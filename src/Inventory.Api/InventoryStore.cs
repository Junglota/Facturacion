using Integration.Contracts;
using Integration.Data;
using Microsoft.Data.Sqlite;
using static Integration.Data.SqliteDb;

namespace Inventory.Api;

public sealed class InventoryStore
{
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS "Artículos" (
            id INTEGER PRIMARY KEY, name TEXT NOT NULL,
            price_cents INTEGER NOT NULL CHECK(price_cents BETWEEN 1 AND 1000000000),
            stock INTEGER NOT NULL CHECK(stock BETWEEN 0 AND 2147483647),
            is_active INTEGER NOT NULL DEFAULT 1 CHECK(is_active IN (0,1)));
        CREATE TABLE IF NOT EXISTS reservations (
            operation_id TEXT PRIMARY KEY, product_id INTEGER REFERENCES "Artículos"(id),
            quantity INTEGER NOT NULL, unit_price_cents INTEGER NOT NULL,
            status TEXT NOT NULL CHECK(status IN ('Reserved','Confirmed','Cancelled')));
        """;
    private readonly SqliteDb database;
    public InventoryStore(string path)
    {
        // Se conserva la tabla original y sus relaciones al adoptar el nombre del dominio.
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 5 }.ToString()))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            db.Open();
            using var tx = db.BeginTransaction(deferred: false);
            using var oldTable = Command(db, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='products'", tx);
            if (oldTable.ExecuteScalar() is not null)
            {
                using var rename = Command(db, "ALTER TABLE products RENAME TO \"Artículos\"", tx);
                rename.ExecuteNonQuery();
            }
            using var columns = Command(db, "SELECT COUNT(*) FROM pragma_table_info('Artículos') WHERE name='is_active'", tx);
            var hasTable = Convert.ToInt32(columns.ExecuteScalar());
            using var exists = Command(db, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='Artículos'", tx);
            if (exists.ExecuteScalar() is not null && hasTable == 0)
            {
                using var add = Command(db, "ALTER TABLE \"Artículos\" ADD COLUMN is_active INTEGER NOT NULL DEFAULT 1 CHECK(is_active IN (0,1))", tx);
                add.ExecuteNonQuery();
            }
            tx.Commit();
        }
        database = new(path, Schema);
        using var connection = database.Open();
        using var view = Command(connection, "CREATE VIEW IF NOT EXISTS products AS SELECT id,name,price_cents,stock FROM \"Artículos\"");
        view.ExecuteNonQuery();
    }
    private static Product Product(SqliteDataReader row) => new(row.GetInt64(0), row.GetString(1), row.GetInt64(2), row.GetInt32(3), row.GetBoolean(4));
    private static Reservation Reservation(SqliteDataReader row) => new(row.GetString(0), row.IsDBNull(1) ? null : row.GetInt64(1), row.GetInt32(2), row.GetInt64(3), row.GetString(4));
    private static Reservation? Find(SqliteConnection db, SqliteTransaction tx, string operation)
    {
        using var command = Command(db, "SELECT * FROM reservations WHERE operation_id=$op", tx, ("$op", operation));
        using var row = command.ExecuteReader();
        return row.Read() ? Reservation(row) : null;
    }
    public List<Product> List(int offset, int limit)
    {
        using var db = database.Open();
        using var command = Command(db, "SELECT * FROM \"Artículos\" ORDER BY id LIMIT $limit OFFSET $offset", null, ("$limit", limit), ("$offset", offset));
        using var rows = command.ExecuteReader();
        var result = new List<Product>();
        while (rows.Read()) result.Add(Product(rows));
        return result;
    }
    public Product? Get(long id)
    {
        using var db = database.Open();
        using var command = Command(db, "SELECT * FROM \"Artículos\" WHERE id=$id", null, ("$id", id));
        using var row = command.ExecuteReader();
        return row.Read() ? Product(row) : null;
    }
    public Product Create(ProductInput input)
    {
        using var db = database.Open();
        using var command = Command(db, "INSERT INTO \"Artículos\"(name,price_cents,stock,is_active) VALUES($name,$price,$stock,$active); SELECT last_insert_rowid();", null,
            ("$name", input.Name.Trim()), ("$price", input.PriceCents), ("$stock", input.Stock), ("$active", input.IsActive ?? true));
        return new((long)command.ExecuteScalar()!, input.Name.Trim(), input.PriceCents, input.Stock, input.IsActive ?? true);
    }
    public Product Update(long id, ProductInput input) => UpdateFields(id, input.Name, input.PriceCents, input.Stock, input.IsActive);
    public Product UpdateArticle(long id, ArticleInput input) => UpdateFields(id, input.Description, input.UnitPriceCents, input.Stock, input.IsActive);
    private Product UpdateFields(long id, string name, long price, int? stock, bool? isActive)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        using var active = Command(db, "SELECT 1 FROM reservations WHERE product_id=$id AND status='Reserved' LIMIT 1", tx, ("$id", id));
        if (active.ExecuteScalar() is not null) throw new BusinessException(409, "El producto tiene reservas pendientes");
        using var command = Command(db, "UPDATE \"Artículos\" SET name=$name,price_cents=$price,stock=COALESCE($stock,stock),is_active=COALESCE($active,is_active) WHERE id=$id", tx,
            ("$name", name.Trim()), ("$price", price), ("$stock", stock), ("$active", isActive), ("$id", id));
        if (command.ExecuteNonQuery() == 0) throw new BusinessException(404, "Producto no encontrado");
        using var query = Command(db, "SELECT * FROM \"Artículos\" WHERE id=$id", tx, ("$id", id));
        using var row = query.ExecuteReader();
        row.Read();
        var result = Product(row);
        row.Close();
        tx.Commit();
        return result;
    }
    public void Delete(long id)
    {
        using var db = database.Open();
        try
        {
            using var command = Command(db, "DELETE FROM \"Artículos\" WHERE id=$id", null, ("$id", id));
            if (command.ExecuteNonQuery() == 0) throw new BusinessException(404, "Producto no encontrado");
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        { throw new BusinessException(409, "El producto tiene reservas o ventas registradas"); }
    }
    public Reservation Reserve(ReservationInput input)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        if (Find(db, tx, input.OperationId) is { } previous)
        {
            if (previous.Status == "Cancelled" || previous.ProductId != input.ProductId || previous.Quantity != input.Quantity)
                throw new BusinessException(409, "La operación fue cancelada o tiene datos diferentes");
            return previous;
        }
        Product product;
        using (var command = Command(db, "SELECT * FROM \"Artículos\" WHERE id=$id", tx, ("$id", input.ProductId)))
        {
            using var row = command.ExecuteReader();
            if (!row.Read()) throw new BusinessException(404, "Producto no encontrado");
            product = Product(row);
        }
        if (!product.IsActive) throw new BusinessException(409, "El artículo está inactivo");
        if (product.Stock < input.Quantity) throw new BusinessException(409, "Existencias insuficientes");
        using var update = Command(db, "UPDATE \"Artículos\" SET stock=stock-$qty WHERE id=$id", tx, ("$qty", input.Quantity), ("$id", input.ProductId));
        update.ExecuteNonQuery();
        using var insert = Command(db, "INSERT INTO reservations VALUES($op,$id,$qty,$price,'Reserved')", tx,
            ("$op", input.OperationId), ("$id", input.ProductId), ("$qty", input.Quantity), ("$price", product.PriceCents));
        insert.ExecuteNonQuery();
        tx.Commit();
        return new(input.OperationId, input.ProductId, input.Quantity, product.PriceCents, "Reserved");
    }
    public Reservation Confirm(string operation)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        var reservation = Find(db, tx, operation) ?? throw new BusinessException(404, "Reserva no encontrada");
        if (reservation.Status == "Cancelled") throw new BusinessException(409, "Reserva cancelada");
        using var command = Command(db, "UPDATE reservations SET status='Confirmed' WHERE operation_id=$op", tx, ("$op", operation));
        command.ExecuteNonQuery();
        tx.Commit();
        return reservation with { Status = "Confirmed" };
    }
    public Reservation Cancel(string operation)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        var reservation = Find(db, tx, operation);
        if (reservation?.Status == "Confirmed") throw new BusinessException(409, "La reserva ya fue confirmada");
        if (reservation?.Status == "Reserved")
        {
            using var release = Command(db, "UPDATE \"Artículos\" SET stock=stock+$qty WHERE id=$id", tx, ("$qty", reservation.Quantity), ("$id", reservation.ProductId));
            release.ExecuteNonQuery();
        }
        // Una cancelación desconocida deja una marca que bloquea reservas tardías.
        using var command = Command(db, "INSERT INTO reservations VALUES($op,NULL,0,0,'Cancelled') ON CONFLICT(operation_id) DO UPDATE SET status='Cancelled'", tx, ("$op", operation));
        command.ExecuteNonQuery();
        tx.Commit();
        return reservation is null ? new(operation, null, 0, 0, "Cancelled") : reservation with { Status = "Cancelled" };
    }
}
