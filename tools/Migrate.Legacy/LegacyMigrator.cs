using Inventory.Api;
using Sales.Api;
using Microsoft.Data.Sqlite;

namespace Migration;

public static class LegacyMigrator
{
    public static void Migrate(string sourcePath, string inventoryPath, string salesPath)
    {
        var source = Path.GetFullPath(sourcePath);
        var inventory = Path.GetFullPath(inventoryPath);
        var sales = Path.GetFullPath(salesPath);
        if (!File.Exists(source)) throw new FileNotFoundException("No existe la base de datos original", source);
        if (inventory.Equals(sales, StringComparison.OrdinalIgnoreCase) || File.Exists(inventory) || File.Exists(sales))
            throw new InvalidOperationException("Los destinos deben ser dos archivos distintos que todavía no existan. Detenga los servicios antes de migrar.");
        var temporaryInventory = inventory + ".migrating-" + Guid.NewGuid().ToString("N");
        var temporarySales = sales + ".migrating-" + Guid.NewGuid().ToString("N");
        try
        {
            _ = new InventoryStore(temporaryInventory);
            _ = new SalesStore(temporarySales);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporaryInventory }.ToString()))
            {
                db.Open();
                using var attach = db.CreateCommand();
                attach.CommandText = "ATTACH DATABASE $source AS legacy; ATTACH DATABASE $sales AS salesdb; PRAGMA foreign_keys=ON;";
                attach.Parameters.AddWithValue("$source", source);
                attach.Parameters.AddWithValue("$sales", temporarySales);
                attach.ExecuteNonQuery();
                using var tx = db.BeginTransaction();
                using var copy = db.CreateCommand();
                copy.Transaction = tx;
                copy.CommandText = """
                    INSERT INTO main."Artículos"(id,name,price_cents,stock)
                        SELECT id,name,price_cents,stock FROM legacy.products;
                    INSERT INTO main.reservations(operation_id,product_id,quantity,unit_price_cents,status)
                        SELECT 'legacy-' || id,product_id,quantity,total_cents / quantity,'Confirmed' FROM legacy.sales;
                    INSERT INTO salesdb.sales(id,product_id,quantity,reference,total_cents,status,operation_id)
                        SELECT id,product_id,quantity,reference,total_cents,'Confirmed','legacy-' || id FROM legacy.sales;
                    """;
                copy.ExecuteNonQuery();
                tx.Commit();
            }
            SqliteConnection.ClearAllPools();
            File.Move(temporaryInventory, inventory);
            File.Move(temporarySales, sales);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(temporaryInventory);
            File.Delete(temporarySales);
        }
    }
}
