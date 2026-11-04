using System.Net.Http.Json;
using Integration.Api;
using Integration.Contracts;
using Inventory.Api;
using Sales.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Integration.Tests;

public sealed class TestCluster : IAsyncLifetime
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "microservices-" + Guid.NewGuid());
    public string InventoryPath => Path.Combine(DirectoryPath, "inventory.db");
    public string SalesPath => Path.Combine(DirectoryPath, "sales.db");
    public WebApplication Inventory { get; private set; } = null!;
    public WebApplication Sales { get; private set; } = null!;
    public WebApplication Gateway { get; private set; } = null!;
    public string InventoryUrl { get; private set; } = "http://127.0.0.1:0";
    private string salesUrl = "http://127.0.0.1:0";
    public bool DropReserveReplies;
    public bool DropConfirmReplies;
    public HttpClient Client { get; private set; } = null!;

    private static void Configure(WebApplicationBuilder builder, string url, Dictionary<string, string?> values)
    {
        builder.WebHost.UseUrls(url);
        builder.Configuration.AddInMemoryCollection(values);
        builder.Logging.ClearProviders();
    }
    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(DirectoryPath);
        await StartInventory();
        await StartSales();
        Gateway = GatewayHost.Build([], builder => Configure(builder, "http://127.0.0.1:0", new() {
            ["START_LOCAL_SERVICES"] = "false", ["API_KEY"] = "test-key", ["SERVICE_API_KEY"] = "internal-key", ["ALLOW_LOCAL_SETUP"] = "true",
            ["Inventory:Url"] = InventoryUrl, ["Sales:Url"] = salesUrl, ["AUTH_DATABASE_PATH"] = Path.Combine(DirectoryPath, "users.db")
        }));
        await Gateway.StartAsync();
        Client = new HttpClient { BaseAddress = new Uri(Address(Gateway)) };
        Client.DefaultRequestHeaders.Add("X-API-Key", "test-key");
    }
    public async Task StartInventory()
    {
        Inventory = InventoryHost.Build([], builder => Configure(builder, InventoryUrl, new() {
            ["DATABASE_PATH"] = InventoryPath, ["SERVICE_API_KEY"] = "internal-key"
        }));
        await Inventory.StartAsync();
        InventoryUrl = Address(Inventory);
    }
    public async Task StopInventory() { await Inventory.StopAsync(); await Inventory.DisposeAsync(); }
    public async Task StartSales()
    {
        Sales = SalesHost.Build([], builder =>
        {
            Configure(builder, salesUrl, new() {
                ["DATABASE_PATH"] = SalesPath, ["SERVICE_API_KEY"] = "internal-key", ["Inventory:Url"] = InventoryUrl
            });
            builder.Services.AddHttpClient(InventoryClient.ClientName).AddHttpMessageHandler(() => new LostResponse(this));
        });
        await Sales.StartAsync();
        salesUrl = Address(Sales);
    }
    public async Task StopSales() { await Sales.StopAsync(); await Sales.DisposeAsync(); }
    public async Task<Product> Product(int stock = 10)
    {
        using var response = await Client.PostAsJsonAsync("/api/v1/products", new ProductInput("Teclado", 150000, stock));
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Product>())!;
    }
    public async Task<Sale> WaitForStatus(long id, string status)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var sale = await Client.GetFromJsonAsync<Sale>($"/api/v1/sales/{id}");
            if (sale?.Status == status) return sale;
            await Task.Delay(100);
        }
        throw new Exception($"La venta {id} no llegó al estado {status}");
    }
    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Gateway.StopAsync(); await Gateway.DisposeAsync();
        await StopSales(); await StopInventory();
        SqliteConnection.ClearAllPools();
        Directory.Delete(DirectoryPath, recursive: true);
    }
    private sealed class LostResponse(TestCluster cluster) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            if ((cluster.DropReserveReplies && path == "/internal/reservations") ||
                (cluster.DropConfirmReplies && path.EndsWith("/confirm")))
            {
                response.Dispose();
                throw new HttpRequestException("Simulación: Inventario confirmó el cambio pero se perdió la respuesta HTTP");
            }
            return response;
        }
    }
}
