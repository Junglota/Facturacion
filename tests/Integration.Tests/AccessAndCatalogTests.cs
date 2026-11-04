using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Integration.Contracts;
using Inventory.Api;
using Integration.Api;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Integration.Tests;

public sealed class AccessAndCatalogTests : IClassFixture<TestCluster>
{
    private readonly TestCluster cluster;
    public AccessAndCatalogTests(TestCluster cluster) => this.cluster = cluster;

    [Fact]
    public async Task UserSessionProtectsTheCatalogsAndAllThreeCrudFlowsWork()
    {
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var browser = new HttpClient(handler) { BaseAddress = cluster.Client.BaseAddress };
        using var denied = await browser.GetAsync("/api/v1/clients");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        browser.DefaultRequestHeaders.Add("X-API-Key", "local-demo-key");
        using var noDefaultKey = await browser.GetAsync("/api/v1/clients");
        Assert.Equal(HttpStatusCode.Unauthorized, noDefaultKey.StatusCode);
        browser.DefaultRequestHeaders.Remove("X-API-Key");
        using (var swagger = JsonDocument.Parse(await browser.GetStringAsync("/swagger/v1/swagger.json")))
        {
            Assert.True(swagger.RootElement.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("Session", out _));
            var loginSecurity = swagger.RootElement.GetProperty("paths").GetProperty("/api/auth/login").GetProperty("post").GetProperty("security");
            Assert.Empty(Assert.Single(loginSecurity.EnumerateArray()).EnumerateObject());
        }
        var credentials = new Credentials("administrador", "una-clave-larga-123");
        using var rebind = new HttpRequestMessage(HttpMethod.Post, "/api/auth/setup") { Content = JsonContent.Create(credentials) };
        rebind.Headers.Host = "attacker.example";
        rebind.Headers.Add("Origin", "http://attacker.example");
        using var blockedRebind = await browser.SendAsync(rebind);
        Assert.Equal(HttpStatusCode.Forbidden, blockedRebind.StatusCode);
        using var blockedSetup = await browser.PostAsJsonAsync("/api/auth/setup", credentials);
        Assert.Equal(HttpStatusCode.Forbidden, blockedSetup.StatusCode);
        browser.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        using var createdUser = await browser.PostAsJsonAsync("/api/auth/setup", credentials);
        Assert.Equal(HttpStatusCode.NoContent, createdUser.StatusCode);
        using var duplicateUser = await browser.PostAsJsonAsync("/api/auth/setup", credentials);
        Assert.Equal(HttpStatusCode.Conflict, duplicateUser.StatusCode);
        using var wrongPassword = await browser.PostAsJsonAsync("/api/auth/login", credentials with { Password = "clave-incorrecta" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        using var login = await browser.PostAsJsonAsync("/api/auth/login", credentials);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("httponly", login.Headers.GetValues("Set-Cookie").Single(), StringComparison.OrdinalIgnoreCase);
        var secondCredentials = new Credentials("vendedor", "otra-clave-segura-123");
        using var addUser = await browser.PostAsJsonAsync("/api/auth/users", secondCredentials);
        Assert.Equal(HttpStatusCode.Created, addUser.StatusCode);
        using var duplicateAccount = await browser.PostAsJsonAsync("/api/auth/users", secondCredentials);
        Assert.Equal(HttpStatusCode.Conflict, duplicateAccount.StatusCode);
        using var regularHandler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var regular = new HttpClient(regularHandler) { BaseAddress = browser.BaseAddress };
        regular.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        using var secondLogin = await regular.PostAsJsonAsync("/api/auth/login", secondCredentials);
        Assert.Equal(HttpStatusCode.OK, secondLogin.StatusCode);
        using var regularRead = await regular.GetAsync("/api/v1/clients");
        Assert.Equal(HttpStatusCode.OK, regularRead.StatusCode);
        using var forbiddenUser = await regular.PostAsJsonAsync("/api/auth/users", new Credentials("tercero", "tercera-clave-segura"));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenUser.StatusCode);
        using (var db = new SqliteConnection($"Data Source={Path.Combine(cluster.DirectoryPath, "users.db")}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT password_hash FROM Users WHERE username='administrador'";
            Assert.NotEqual(credentials.Password, command.ExecuteScalar());
        }

        var input = new ClientInput("Empresa SRL", "101-00000-1", "1.1.02", true);
        browser.DefaultRequestHeaders.Remove("X-Requested-With");
        using var csrf = await browser.PostAsJsonAsync("/api/v1/clients", input);
        Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        browser.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        using var clientResponse = await browser.PostAsJsonAsync("/api/v1/clients", input);
        Assert.Equal(HttpStatusCode.Created, clientResponse.StatusCode);
        var client = (await clientResponse.Content.ReadFromJsonAsync<Client>())!;
        Assert.Equal(input.TaxId, (await browser.GetFromJsonAsync<Client>($"/api/v1/clients/{client.Id}"))!.TaxId);
        using var clientUpdate = await browser.PutAsJsonAsync($"/api/v1/clients/{client.Id}", input with { Name = "Empresa Nueva", IsActive = false });
        Assert.False((await clientUpdate.Content.ReadFromJsonAsync<Client>())!.IsActive);
        Assert.Contains((await browser.GetFromJsonAsync<List<Client>>("/api/v1/clients"))!, x => x.Id == client.Id && x.Name == "Empresa Nueva");
        using var clientDelete = await browser.DeleteAsync($"/api/v1/clients/{client.Id}");
        Assert.Equal(HttpStatusCode.NoContent, clientDelete.StatusCode);
        using var clientMissing = await browser.GetAsync($"/api/v1/clients/{client.Id}");
        Assert.Equal(HttpStatusCode.NotFound, clientMissing.StatusCode);

        using var invalidSeller = await browser.PostAsJsonAsync("/api/v1/sellers", new SellerInput("Ana", 100.01m, true));
        Assert.Equal((HttpStatusCode)422, invalidSeller.StatusCode);
        using var sellerResponse = await browser.PostAsJsonAsync("/api/v1/sellers", new SellerInput("Ana", 12.35m, true));
        Assert.Equal(HttpStatusCode.Created, sellerResponse.StatusCode);
        var seller = (await sellerResponse.Content.ReadFromJsonAsync<Seller>())!;
        Assert.Equal(12.35m, (await browser.GetFromJsonAsync<Seller>($"/api/v1/sellers/{seller.Id}"))!.CommissionPercent);
        using var sellerUpdate = await browser.PutAsJsonAsync($"/api/v1/sellers/{seller.Id}", new SellerInput("Ana María", 15.50m, false));
        Assert.False((await sellerUpdate.Content.ReadFromJsonAsync<Seller>())!.IsActive);
        Assert.Contains((await browser.GetFromJsonAsync<List<Seller>>("/api/v1/sellers"))!, x => x.Id == seller.Id);
        using var sellerDelete = await browser.DeleteAsync($"/api/v1/sellers/{seller.Id}");
        Assert.Equal(HttpStatusCode.NoContent, sellerDelete.StatusCode);

        using var articleResponse = await browser.PostAsJsonAsync("/api/v1/articles", new ArticleInput("Teclado", 150000, false, 10));
        Assert.Equal(HttpStatusCode.Created, articleResponse.StatusCode);
        var article = (await articleResponse.Content.ReadFromJsonAsync<Article>())!;
        Assert.False((await browser.GetFromJsonAsync<Article>($"/api/v1/articles/{article.Id}"))!.IsActive);
        using var inactiveSale = await browser.PostAsJsonAsync("/api/v1/sales", new SaleInput(article.Id, 3, "INACTIVO-1"));
        Assert.Equal(HttpStatusCode.Conflict, inactiveSale.StatusCode);
        using var articleUpdate = await browser.PutAsJsonAsync($"/api/v1/articles/{article.Id}", new ArticleInput("Teclado nuevo", 150000, true));
        var updated = (await articleUpdate.Content.ReadFromJsonAsync<Article>())!;
        Assert.True(updated.IsActive);
        Assert.Equal(10, updated.Stock);
        Assert.Contains((await browser.GetFromJsonAsync<List<Article>>("/api/v1/articles"))!, x => x.Id == article.Id);
        using var activeSale = await browser.PostAsJsonAsync("/api/v1/sales", new SaleInput(article.Id, 3, "ACTIVO-1"));
        Assert.Equal(HttpStatusCode.Created, activeSale.StatusCode);
        Assert.Equal(7, (await browser.GetFromJsonAsync<Article>($"/api/v1/articles/{article.Id}"))!.Stock);
        using var blockedDelete = await browser.DeleteAsync($"/api/v1/articles/{article.Id}");
        Assert.Equal(HttpStatusCode.Conflict, blockedDelete.StatusCode);
        using var disposable = await browser.PostAsJsonAsync("/api/v1/articles", new ArticleInput("Mouse", 50000, true));
        var disposableArticle = (await disposable.Content.ReadFromJsonAsync<Article>())!;
        using var deleted = await browser.DeleteAsync($"/api/v1/articles/{disposableArticle.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var logout = await browser.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var afterLogout = await browser.GetAsync("/api/v1/sellers");
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public void ExistingSingleUserDatabaseCanBeExtendedWithoutChangingItsPassword()
    {
        var path = Path.Combine(cluster.DirectoryPath, Guid.NewGuid() + ".db");
        var password = "una-clave-anterior-123";
        using (var db = new SqliteConnection($"Data Source={path}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE Users(id INTEGER PRIMARY KEY CHECK(id=1), username TEXT NOT NULL COLLATE NOCASE UNIQUE, password_hash TEXT NOT NULL); INSERT INTO Users VALUES(1,'admin',$hash);";
            command.Parameters.AddWithValue("$hash", new PasswordHasher<string>().HashPassword("admin", password));
            command.ExecuteNonQuery();
        }
        var users = new UserStore(path);
        Assert.True(users.Authenticate(new("admin", password))!.IsAdmin);
        users.Add(new("operador", "clave-operador-segura"));
        Assert.False(users.Authenticate(new("operador", "clave-operador-segura"))!.IsAdmin);
    }

    [Fact]
    public void ExistingInventorySchemaIsMigratedWithoutLosingReservations()
    {
        var path = Path.Combine(cluster.DirectoryPath, Guid.NewGuid() + ".db");
        using (var db = new SqliteConnection($"Data Source={path}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE products(id INTEGER PRIMARY KEY, name TEXT NOT NULL, price_cents INTEGER NOT NULL, stock INTEGER NOT NULL);
                CREATE TABLE reservations(operation_id TEXT PRIMARY KEY, product_id INTEGER REFERENCES products(id), quantity INTEGER NOT NULL, unit_price_cents INTEGER NOT NULL, status TEXT NOT NULL);
                INSERT INTO products VALUES(4,'Teclado',150000,7);
                INSERT INTO reservations VALUES('legacy-1',4,3,150000,'Confirmed');
                """;
            command.ExecuteNonQuery();
        }
        var store = new InventoryStore(path);
        Assert.True(store.Get(4)!.IsActive);
        Assert.Equal(7, store.Get(4)!.Stock);
        Assert.Throws<BusinessException>(() => store.Delete(4));
        using var check = new SqliteConnection($"Data Source={path}");
        check.Open();
        using var verify = check.CreateCommand();
        verify.CommandText = "PRAGMA foreign_key_check";
        using var rows = verify.ExecuteReader();
        Assert.False(rows.Read());
    }
}
