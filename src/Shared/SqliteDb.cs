using Integration.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;

namespace Integration.Data;

// Se comparte código de conexión, nunca una conexión ni una base entre servicios.
public sealed class SqliteDb
{
    private readonly string connectionString;
    public SqliteDb(string path, string schema)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 5 }.ToString();
        using var db = Open();
        using var command = Command(db, schema);
        command.ExecuteNonQuery();
    }
    public SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        using var command = Command(db, "PRAGMA foreign_keys=ON");
        command.ExecuteNonQuery();
        return db;
    }
    public static SqliteCommand Command(SqliteConnection db, string sql, SqliteTransaction? transaction = null,
        params (string Name, object? Value)[] parameters)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    public static void UseErrors(WebApplication app) => app.Use(async (context, next) =>
    {
        try { await next(context); }
        catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6)
        { await ApiDefaults.Error(503, "Base de datos ocupada; reintente la operación").ExecuteAsync(context); }
    });
}
