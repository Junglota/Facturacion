using Migration;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Integration.Tests;

public sealed class LegacyMigrationTests
{
    [Fact]
    public void MigrationPreservesIdsStockAndTotalsWithoutOverwritingSourceOrDestinations()
    {
        var directory = Path.Combine(Path.GetTempPath(), "legacy-migration-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "integration.db");
        var inventory = Path.Combine(directory, "inventory.db");
        var sales = Path.Combine(directory, "sales.db");
        try
        {
            using (var db = new SqliteConnection($"Data Source={source}"))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = """
                    CREATE TABLE products(id INTEGER PRIMARY KEY,name TEXT,price_cents INTEGER,stock INTEGER);
                    CREATE TABLE sales(id INTEGER PRIMARY KEY,product_id INTEGER,quantity INTEGER,reference TEXT,total_cents INTEGER);
                    INSERT INTO products VALUES(4,'Teclado',150000,7);
                    INSERT INTO sales VALUES(8,4,3,'V-LEGACY',450000);
                    """;
                command.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            var before = File.ReadAllBytes(source);
            LegacyMigrator.Migrate(source, inventory, sales);
            Assert.Equal(before, File.ReadAllBytes(source));
            using (var db = new SqliteConnection($"Data Source={inventory}"))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "SELECT stock FROM products WHERE id=4";
                Assert.Equal(7L, command.ExecuteScalar());
                command.CommandText = "SELECT status FROM reservations WHERE operation_id='legacy-8'";
                Assert.Equal("Confirmed", command.ExecuteScalar());
            }
            using (var db = new SqliteConnection($"Data Source={sales}"))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "SELECT total_cents FROM sales WHERE id=8";
                Assert.Equal(450000L, command.ExecuteScalar());
            }
            Assert.Throws<InvalidOperationException>(() => LegacyMigrator.Migrate(source, inventory, sales));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
