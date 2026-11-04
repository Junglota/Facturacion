using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Integration.Contracts;
using Integration.Api;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Integration.Tests;

public sealed class MicroserviceTests : IClassFixture<TestCluster>
{
    private readonly TestCluster cluster;
    public MicroserviceTests(TestCluster cluster) => this.cluster = cluster;
    private HttpClient Client => cluster.Client;
    private static string Reference() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task ProductsAndSalesExchangeDataWithoutSharingTables()
    {
        var product = await cluster.Product();
        var reference = Reference();
        using var response = await Client.PostAsJsonAsync("/api/v1/sales", new SaleInput(product.Id, 3, reference));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var sale = await response.Content.ReadFromJsonAsync<Sale>();
        Assert.Equal("Confirmed", sale!.Status);
        Assert.Equal(450000, sale.TotalCents);
        Assert.Equal(7, (await Client.GetFromJsonAsync<Product>($"/api/v1/products/{product.Id}"))!.Stock);
        using var inventory = new SqliteConnection($"Data Source={cluster.InventoryPath}");
        inventory.Open();
        using var query = inventory.CreateCommand();
        query.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='sales'";
        Assert.Equal(0L, query.ExecuteScalar());
        using var sales = new SqliteConnection($"Data Source={cluster.SalesPath}");
        sales.Open();
        using var salesQuery = sales.CreateCommand();
        salesQuery.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='products'";
        Assert.Equal(0L, salesQuery.ExecuteScalar());
    }

    [Fact]
    public async Task RetryingTheSameSaleReturnsOriginalWithoutDiscountingAgain()
    {
        var product = await cluster.Product();
        var input = new SaleInput(product.Id, 3, Reference());
        using var first = await Client.PostAsJsonAsync("/api/v1/sales", input);
        var original = await first.Content.ReadFromJsonAsync<Sale>();
        using var second = await Client.PostAsJsonAsync("/api/v1/sales", input);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(original!.Id, (await second.Content.ReadFromJsonAsync<Sale>())!.Id);
        using var changed = await Client.PostAsJsonAsync("/api/v1/sales", input with { Quantity = 2 });
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(7, (await Client.GetFromJsonAsync<Product>($"/api/v1/products/{product.Id}"))!.Stock);
    }

    [Fact]
    public async Task ConcurrentSalesCannotConsumeTheSameUnit()
    {
        var product = await cluster.Product(1);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Client.PostAsJsonAsync("/api/v1/sales", new SaleInput(product.Id, 1, Reference()))));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Equal(7, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(0, (await Client.GetFromJsonAsync<Product>($"/api/v1/products/{product.Id}"))!.Stock);
        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task CrudPreservesHistoryAndHistoricalTotals()
    {
        var product = await cluster.Product();
        using var update = await Client.PutAsJsonAsync($"/api/v1/products/{product.Id}", new ProductInput("Mouse", 50000, 4));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using var response = await Client.PostAsJsonAsync("/api/v1/sales", new SaleInput(product.Id, 1, Reference()));
        var sale = await response.Content.ReadFromJsonAsync<Sale>();
        using var blocked = await Client.DeleteAsync($"/api/v1/products/{product.Id}");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        using var changePrice = await Client.PutAsJsonAsync($"/api/v1/products/{product.Id}", new ProductInput("Mouse", 90000, 3));
        Assert.Equal(HttpStatusCode.OK, changePrice.StatusCode);
        Assert.Equal(50000, (await Client.GetFromJsonAsync<Sale>($"/api/v1/sales/{sale!.Id}"))!.TotalCents);
        var other = await cluster.Product();
        using var removed = await Client.DeleteAsync($"/api/v1/products/{other.Id}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    }

    [Fact]
    public async Task PendingSaleRecoversAfterInventoryRestarts()
    {
        var product = await cluster.Product();
        await cluster.StopInventory();
        Sale? pending;
        try
        {
            using var response = await Client.PostAsJsonAsync("/api/v1/sales", new SaleInput(product.Id, 3, Reference()));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            pending = await response.Content.ReadFromJsonAsync<Sale>();
            Assert.Equal("Pending", pending!.Status);
        }
        finally { await cluster.StartInventory(); }
        var recovered = await cluster.WaitForStatus(pending.Id, "Confirmed");
        Assert.Equal(450000, recovered.TotalCents);
        Assert.Equal(7, (await Client.GetFromJsonAsync<Product>($"/api/v1/products/{product.Id}"))!.Stock);
    }

    [Fact]
    public async Task LostConfirmationRecoversAfterSalesRestarts()
    {
        var product = await cluster.Product();
        cluster.DropConfirmReplies = true;
        Sale? pending;
        try
        {
            using var response = await Client.PostAsJsonAsync("/api/v1/sales", new SaleInput(product.Id, 3, Reference()));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            pending = await response.Content.ReadFromJsonAsync<Sale>();
            Assert.Equal("Confirming", pending!.Status);
            Assert.Equal(7, (await Client.GetFromJsonAsync<Product>($"/api/v1/products/{product.Id}"))!.Stock);
        }
        finally { cluster.DropConfirmReplies = false; }
        await cluster.StopSales();
        await cluster.StartSales();
        await cluster.WaitForStatus(pending.Id, "Confirmed");
        Assert.Equal(7, (await Client.GetFromJsonAsync<Product>($"/api/v1/products/{product.Id}"))!.Stock);
    }

    [Fact]
    public async Task CancellationReleasesAReservationOnlyOnce()
    {
        var product = await cluster.Product();
        cluster.DropReserveReplies = true;
        try
        {
            using var response = await Client.PostAsJsonAsync("/api/v1/sales", new SaleInput(product.Id, 3, Reference()));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var pending = await response.Content.ReadFromJsonAsync<Sale>();
            Assert.Equal("Pending", pending!.Status);
            Assert.Equal(7, (await Client.GetFromJsonAsync<Product>($"/api/v1/products/{product.Id}"))!.Stock);
            using var cancelled = await Client.PostAsync($"/api/v1/sales/{pending.Id}/cancel", null);
            Assert.Equal("Cancelled", (await cancelled.Content.ReadFromJsonAsync<Sale>())!.Status);
            using var repeated = await Client.PostAsync($"/api/v1/sales/{pending.Id}/cancel", null);
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
            Assert.Equal(10, (await Client.GetFromJsonAsync<Product>($"/api/v1/products/{product.Id}"))!.Stock);
        }
        finally { cluster.DropReserveReplies = false; }
    }

    [Fact]
    public async Task CancellingUnknownReservationBlocksLateRequests()
    {
        var product = await cluster.Product();
        using var internalClient = new HttpClient { BaseAddress = new Uri(cluster.InventoryUrl) };
        internalClient.DefaultRequestHeaders.Add("X-API-Key", "internal-key");
        var operation = Reference();
        using var cancel = await internalClient.PostAsync($"/internal/reservations/{operation}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        using var late = await internalClient.PostAsJsonAsync("/internal/reservations", new ReservationInput(operation, product.Id, 1));
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal(10, (await Client.GetFromJsonAsync<Product>($"/api/v1/products/{product.Id}"))!.Stock);
    }

    [Fact]
    public async Task GatewayKeepsProductsAvailableWhenSalesIsDown()
    {
        await cluster.StopSales();
        try
        {
            using var products = await Client.GetAsync("/api/v1/products");
            Assert.Equal(HttpStatusCode.OK, products.StatusCode);
            using var sales = await Client.GetAsync("/api/v1/sales");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, sales.StatusCode);
        }
        finally { await cluster.StartSales(); }
    }

    [Fact]
    public async Task InventorySerializesConcurrentReservationsFromIndependentClients()
    {
        var product = await cluster.Product(1);
        using var service = new HttpClient { BaseAddress = new Uri(cluster.InventoryUrl) };
        service.DefaultRequestHeaders.Add("X-API-Key", "internal-key");
        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            service.PostAsJsonAsync("/internal/reservations", new ReservationInput(Reference(), product.Id, 1))));
        Assert.Single(replies, reply => reply.StatusCode == HttpStatusCode.OK);
        Assert.Equal(7, replies.Count(reply => reply.StatusCode == HttpStatusCode.Conflict));
        using var blocked = await Client.PutAsJsonAsync($"/api/v1/products/{product.Id}", new ProductInput("Otro", 100, 10));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var reservation = await replies.Single(reply => reply.IsSuccessStatusCode).Content.ReadFromJsonAsync<Reservation>();
        using var first = await service.PostAsync($"/internal/reservations/{reservation!.OperationId}/confirm", null);
        using var repeated = await service.PostAsync($"/internal/reservations/{reservation.OperationId}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        using var cancel = await service.PostAsync($"/internal/reservations/{reservation.OperationId}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
        Assert.Equal(0, (await Client.GetFromJsonAsync<Product>($"/api/v1/products/{product.Id}"))!.Stock);
        foreach (var reply in replies) reply.Dispose();
    }

    [Fact]
    public async Task LocalStartupRefusesToUseAnotherInstancesInventoryPort()
    {
        await using var gateway = GatewayHost.Build([], builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
                ["START_LOCAL_SERVICES"] = "true", ["Inventory:Url"] = cluster.InventoryUrl
                , ["AUTH_DATABASE_PATH"] = Path.Combine(cluster.DirectoryPath, "other-users.db")
            });
            builder.Logging.ClearProviders();
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.StartAsync());
        Assert.Contains("ocupado", error.Message);
        using var response = await Client.GetAsync("/api/v1/products");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ValidationCorsAndServiceAuthenticationRemainEnforced()
    {
        using var anonymous = new HttpClient { BaseAddress = Client.BaseAddress };
        using var unauthorized = await anonymous.GetAsync("/api/v1/products");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var malformed = await Client.PostAsync("/api/v1/products", new StringContent("{", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var invalid = await Client.PostAsJsonAsync("/api/v1/products", new ProductInput("", -1, -1));
        Assert.Equal((HttpStatusCode)422, invalid.StatusCode);
        using var missing = await Client.PostAsJsonAsync("/api/v1/products", new { name = "Mouse", priceCents = 100 });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/health");
        preflight.Headers.Add("Origin", "http://localhost:5173");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", "x-api-key");
        using var cors = await anonymous.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, cors.StatusCode);
        Assert.True(cors.Headers.Contains("Access-Control-Allow-Origin"));
        using var service = new HttpClient { BaseAddress = new Uri(cluster.InventoryUrl) };
        service.DefaultRequestHeaders.Add("X-API-Key", "test-key");
        using var denied = await service.GetAsync("/api/v1/products");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var docs = await Client.GetAsync("/swagger/v1/swagger.json");
        using var json = JsonDocument.Parse(await docs.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("paths").TryGetProperty("/api/v1/sales/{id}/cancel", out _));
    }
}
