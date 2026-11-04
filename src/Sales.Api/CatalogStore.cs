using Integration.Contracts;
using Integration.Data;
using Microsoft.Data.Sqlite;
using static Integration.Data.SqliteDb;

namespace Sales.Api;

public sealed class CatalogStore
{
    private readonly SqliteDb database;
    public CatalogStore(string path) => database = new(path, """
        CREATE TABLE IF NOT EXISTS Clientes (
            id INTEGER PRIMARY KEY, name TEXT NOT NULL, tax_id TEXT NOT NULL,
            accounting_account TEXT NOT NULL, is_active INTEGER NOT NULL CHECK(is_active IN (0,1)));
        CREATE TABLE IF NOT EXISTS Vendedores (
            id INTEGER PRIMARY KEY, name TEXT NOT NULL,
            commission_basis_points INTEGER NOT NULL CHECK(commission_basis_points BETWEEN 0 AND 10000),
            is_active INTEGER NOT NULL CHECK(is_active IN (0,1)));
        """);
    private static Client ReadClient(SqliteDataReader row) => new(row.GetInt64(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetBoolean(4));
    private static Seller ReadSeller(SqliteDataReader row) => new(row.GetInt64(0), row.GetString(1), row.GetInt32(2) / 100m, row.GetBoolean(3));
    public List<Client> Clients(int offset, int limit)
    {
        using var db = database.Open();
        using var cmd = Command(db, "SELECT * FROM Clientes ORDER BY id LIMIT $limit OFFSET $offset", null, ("$limit", limit), ("$offset", offset));
        using var row = cmd.ExecuteReader();
        var result = new List<Client>();
        while (row.Read()) result.Add(ReadClient(row));
        return result;
    }
    public Client? Client(long id)
    {
        using var db = database.Open();
        using var cmd = Command(db, "SELECT * FROM Clientes WHERE id=$id", null, ("$id", id));
        using var row = cmd.ExecuteReader();
        return row.Read() ? ReadClient(row) : null;
    }
    public Client SaveClient(long? id, ClientInput input)
    {
        using var db = database.Open();
        using var cmd = Command(db, id is null
            ? "INSERT INTO Clientes(name,tax_id,accounting_account,is_active) VALUES($name,$tax,$account,$active); SELECT last_insert_rowid();"
            : "UPDATE Clientes SET name=$name,tax_id=$tax,accounting_account=$account,is_active=$active WHERE id=$id RETURNING id", null,
            ("$id", id), ("$name", input.Name.Trim()), ("$tax", input.TaxId.Trim()), ("$account", input.AccountingAccount.Trim()), ("$active", input.IsActive));
        var saved = cmd.ExecuteScalar();
        if (saved is null) throw new BusinessException(404, "Cliente no encontrado");
        return new((long)saved, input.Name.Trim(), input.TaxId.Trim(), input.AccountingAccount.Trim(), input.IsActive);
    }
    public List<Seller> Sellers(int offset, int limit)
    {
        using var db = database.Open();
        using var cmd = Command(db, "SELECT * FROM Vendedores ORDER BY id LIMIT $limit OFFSET $offset", null, ("$limit", limit), ("$offset", offset));
        using var row = cmd.ExecuteReader();
        var result = new List<Seller>();
        while (row.Read()) result.Add(ReadSeller(row));
        return result;
    }
    public Seller? Seller(long id)
    {
        using var db = database.Open();
        using var cmd = Command(db, "SELECT * FROM Vendedores WHERE id=$id", null, ("$id", id));
        using var row = cmd.ExecuteReader();
        return row.Read() ? ReadSeller(row) : null;
    }
    public Seller SaveSeller(long? id, SellerInput input)
    {
        using var db = database.Open();
        using var cmd = Command(db, id is null
            ? "INSERT INTO Vendedores(name,commission_basis_points,is_active) VALUES($name,$commission,$active); SELECT last_insert_rowid();"
            : "UPDATE Vendedores SET name=$name,commission_basis_points=$commission,is_active=$active WHERE id=$id RETURNING id", null,
            ("$id", id), ("$name", input.Name.Trim()), ("$commission", (int)(input.CommissionPercent * 100)), ("$active", input.IsActive));
        var saved = cmd.ExecuteScalar();
        if (saved is null) throw new BusinessException(404, "Vendedor no encontrado");
        return new((long)saved, input.Name.Trim(), input.CommissionPercent, input.IsActive);
    }
    public void DeleteClient(long id) => Delete("Clientes", id, "Cliente");
    public void DeleteSeller(long id) => Delete("Vendedores", id, "Vendedor");
    private void Delete(string table, long id, string label)
    {
        using var db = database.Open();
        using var cmd = Command(db, $"DELETE FROM {table} WHERE id=$id", null, ("$id", id));
        if (cmd.ExecuteNonQuery() == 0) throw new BusinessException(404, $"{label} no encontrado");
    }
}
