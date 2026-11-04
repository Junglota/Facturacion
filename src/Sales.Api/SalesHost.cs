using Integration.Contracts;
using Integration.Data;

namespace Sales.Api;

public static class SalesHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddApiDefaults("Microservicio de Ventas");
        builder.Services.AddSingleton(services => new SalesStore(services.GetRequiredService<IConfiguration>()["DATABASE_PATH"] ?? "data/sales.db"));
        builder.Services.AddSingleton(services => new CatalogStore(services.GetRequiredService<IConfiguration>()["DATABASE_PATH"] ?? "data/sales.db"));
        builder.Services.AddHttpClient(InventoryClient.ClientName, (services, client) =>
        {
            var config = services.GetRequiredService<IConfiguration>();
            client.BaseAddress = new Uri(config["Inventory:Url"] ?? "http://127.0.0.1:62711");
            client.DefaultRequestHeaders.Add("X-API-Key", config["SERVICE_API_KEY"] ?? "local-service-key");
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        builder.Services.AddSingleton<InventoryClient>();
        builder.Services.AddSingleton<SaleCoordinator>();
        builder.Services.AddHostedService<SupervisorMonitor>();
        builder.Services.AddHostedService<SaleRecovery>();
        configure?.Invoke(builder);
        var app = builder.Build();
        SqliteDb.UseErrors(app);
        app.UseApiDefaults(builder.Configuration["SERVICE_API_KEY"] ?? "local-service-key");
        app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "sales" }));
        app.MapGet("/api/v1/clients", (CatalogStore store, int offset = 0, int limit = 100) =>
            ApiDefaults.ValidPage(offset, limit) ? Results.Ok(store.Clients(offset, limit)) : ApiDefaults.Error(422, "Paginación inválida"));
        app.MapGet("/api/v1/clients/{id:long}", (long id, CatalogStore store) =>
            store.Client(id) is { } client ? Results.Ok(client) : ApiDefaults.Error(404, "Cliente no encontrado"));
        app.MapPost("/api/v1/clients", (ClientInput input, CatalogStore store) =>
        {
            if (!ValidClient(input)) return ApiDefaults.Error(422, "Nombre, RNC o cédula y cuenta contable son obligatorios");
            var client = store.SaveClient(null, input);
            return Results.Created($"/api/v1/clients/{client.Id}", client);
        });
        app.MapPut("/api/v1/clients/{id:long}", (long id, ClientInput input, CatalogStore store) =>
            ValidClient(input) ? Results.Ok(store.SaveClient(id, input)) : ApiDefaults.Error(422, "Cliente inválido"));
        app.MapDelete("/api/v1/clients/{id:long}", (long id, CatalogStore store) => { store.DeleteClient(id); return Results.NoContent(); });
        app.MapGet("/api/v1/sellers", (CatalogStore store, int offset = 0, int limit = 100) =>
            ApiDefaults.ValidPage(offset, limit) ? Results.Ok(store.Sellers(offset, limit)) : ApiDefaults.Error(422, "Paginación inválida"));
        app.MapGet("/api/v1/sellers/{id:long}", (long id, CatalogStore store) =>
            store.Seller(id) is { } seller ? Results.Ok(seller) : ApiDefaults.Error(404, "Vendedor no encontrado"));
        app.MapPost("/api/v1/sellers", (SellerInput input, CatalogStore store) =>
        {
            if (!ValidSeller(input)) return ApiDefaults.Error(422, "Nombre obligatorio y comisión entre 0 y 100, con hasta dos decimales");
            var seller = store.SaveSeller(null, input);
            return Results.Created($"/api/v1/sellers/{seller.Id}", seller);
        });
        app.MapPut("/api/v1/sellers/{id:long}", (long id, SellerInput input, CatalogStore store) =>
            ValidSeller(input) ? Results.Ok(store.SaveSeller(id, input)) : ApiDefaults.Error(422, "Vendedor inválido"));
        app.MapDelete("/api/v1/sellers/{id:long}", (long id, CatalogStore store) => { store.DeleteSeller(id); return Results.NoContent(); });
        app.MapGet("/api/v1/sales", (SalesStore store, int offset = 0, int limit = 100) =>
            ApiDefaults.ValidPage(offset, limit) ? Results.Ok(store.List(offset, limit)) : ApiDefaults.Error(422, "Paginación inválida")).Produces<List<Sale>>();
        app.MapGet("/api/v1/sales/{id:long}", (long id, SalesStore store) =>
            store.Get(id) is { } operation ? Results.Ok(operation.Sale) : ApiDefaults.Error(404, "Venta no encontrada")).Produces<Sale>();
        app.MapPost("/api/v1/sales", async (SaleInput input, SaleCoordinator coordinator, HttpContext context) =>
        {
            if (!ApiDefaults.Valid(input)) return ApiDefaults.Error(422, "Producto y cantidad positivos; referencia de 1–100 caracteres");
            var result = await coordinator.Create(input, context.RequestAborted);
            var sale = result.Sale;
            context.Response.Headers.Location = $"/api/v1/sales/{sale.Id}";
            return Results.Json(sale, statusCode: sale.Status == "Confirmed" ? (result.Replayed ? 200 : 201) : 202);
        }).Produces<Sale>(201).Produces<Sale>(200).Produces<Sale>(202);
        app.MapPost("/api/v1/sales/{id:long}/cancel", async (long id, SaleCoordinator coordinator, HttpContext context) =>
        {
            var sale = await coordinator.Cancel(id, context.RequestAborted);
            return Results.Json(sale, statusCode: sale.Status == "Cancelling" ? 202 : 200);
        }).Produces<Sale>().Produces<Sale>(202);
        return app;
    }
    private static bool HasText(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= max;
    private static bool ValidClient(ClientInput input) => HasText(input.Name, 150) && HasText(input.TaxId, 20) && HasText(input.AccountingAccount, 50);
    private static bool ValidSeller(SellerInput input) => HasText(input.Name, 150) && input.CommissionPercent is >= 0 and <= 100
        && decimal.Round(input.CommissionPercent, 2) == input.CommissionPercent;
}
