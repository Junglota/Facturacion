using Integration.Contracts;
using Integration.Data;
using Microsoft.AspNetCore.Identity;
using static Integration.Data.SqliteDb;

namespace Integration.Api;

public sealed class UserStore
{
    private readonly SqliteDb database;
    private readonly PasswordHasher<string> hasher = new();
    private static readonly string DummyHash = new PasswordHasher<string>().HashPassword("dummy", "not-a-real-password");
    public UserStore(string path)
    {
        database = new(path, """
        CREATE TABLE IF NOT EXISTS Users (
            id INTEGER PRIMARY KEY, username TEXT NOT NULL COLLATE NOCASE UNIQUE, password_hash TEXT NOT NULL);
        """);
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        using var schema = Command(db, "SELECT sql FROM sqlite_master WHERE type='table' AND name='Users'", tx);
        var sql = Convert.ToString(schema.ExecuteScalar()) ?? "";
        if (sql.Replace(" ", "").Contains("CHECK(id=1)", StringComparison.OrdinalIgnoreCase))
        {
            using var migration = Command(db, """
                CREATE TABLE Users_v2 (id INTEGER PRIMARY KEY, username TEXT NOT NULL COLLATE NOCASE UNIQUE, password_hash TEXT NOT NULL);
                INSERT INTO Users_v2(id,username,password_hash) SELECT id,username,password_hash FROM Users;
                DROP TABLE Users;
                ALTER TABLE Users_v2 RENAME TO Users;
                """, tx);
            migration.ExecuteNonQuery();
        }
        tx.Commit();
    }
    public bool Initialized()
    {
        using var db = database.Open();
        using var command = Command(db, "SELECT COUNT(*) FROM Users");
        return (long)command.ExecuteScalar()! > 0;
    }
    public static bool Valid(Credentials input) => !string.IsNullOrWhiteSpace(input.Username)
        && input.Username.Trim().Length is >= 3 and <= 50 && input.Password is { Length: >= 10 and <= 200 };
    public void Initialize(Credentials input)
    {
        if (!Valid(input)) throw new BusinessException(422, "Usuario de 3 a 50 caracteres y contraseña de 10 a 200 caracteres");
        var username = input.Username.Trim();
        var hash = hasher.HashPassword(username, input.Password);
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        using var exists = Command(db, "SELECT 1 FROM Users LIMIT 1", tx);
        if (exists.ExecuteScalar() is not null) throw new BusinessException(409, "El usuario inicial ya está configurado");
        using var insert = Command(db, "INSERT INTO Users(id,username,password_hash) VALUES(1,$username,$hash)", tx,
            ("$username", username), ("$hash", hash));
        insert.ExecuteNonQuery();
        tx.Commit();
    }
    public void Add(Credentials input)
    {
        if (!Valid(input)) throw new BusinessException(422, "Usuario de 3 a 50 caracteres y contraseña de 10 a 200 caracteres");
        var username = input.Username.Trim();
        var hash = hasher.HashPassword(username, input.Password);
        using var db = database.Open();
        try
        {
            using var insert = Command(db, "INSERT INTO Users(username,password_hash) VALUES($username,$hash)", null,
                ("$username", username), ("$hash", hash));
            insert.ExecuteNonQuery();
        }
        catch (Microsoft.Data.Sqlite.SqliteException error) when (error.SqliteErrorCode == 19)
        { throw new BusinessException(409, "El usuario ya existe"); }
    }
    public LoginUser? Authenticate(Credentials input)
    {
        if (string.IsNullOrWhiteSpace(input.Username) || input.Password is null || input.Password.Length > 200) return null;
        using var db = database.Open();
        using var cmd = Command(db, "SELECT id,username,password_hash FROM Users WHERE username=$username", null, ("$username", input.Username.Trim()));
        using var row = cmd.ExecuteReader();
        if (!row.Read())
        {
            hasher.VerifyHashedPassword("dummy", DummyHash, input.Password);
            return null;
        }
        var username = row.GetString(1);
        return hasher.VerifyHashedPassword(username, row.GetString(2), input.Password) == PasswordVerificationResult.Failed
            ? null : new(username, row.GetInt64(0) == 1);
    }
}

public record LoginUser(string Username, bool IsAdmin);
