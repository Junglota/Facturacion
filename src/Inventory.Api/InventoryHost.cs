using Integration.Contracts;
using Integration.Data;

namespace Inventory.Api;

public static class InventoryHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        builder.AddApiDefaults("Microservicio de Inventario");
        builder.Services.AddHostedService<SupervisorMonitor>();
        builder.Services.AddSingleton(services => new InventoryStore(services.GetRequiredService<IConfiguration>()["DATABASE_PATH"] ?? "data/inventory.db"));
        var app = builder.Build();
        SqliteDb.UseErrors(app);
        app.UseApiDefaults(builder.Configuration["SERVICE_API_KEY"] ?? "local-service-key");
        app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "inventory" }));
        app.MapGet("/api/v1/products", (InventoryStore store, int offset = 0, int limit = 100) =>
            ApiDefaults.ValidPage(offset, limit) ? Results.Ok(store.List(offset, limit)) : ApiDefaults.Error(422, "Paginación inválida"))
            .Produces<List<Product>>();
        app.MapGet("/api/v1/products/{id:long}", (long id, InventoryStore store) =>
            store.Get(id) is { } product ? Results.Ok(product) : ApiDefaults.Error(404, "Producto no encontrado")).Produces<Product>();
        app.MapPost("/api/v1/products", (ProductInput input, InventoryStore store) =>
        {
            if (!ApiDefaults.Valid(input)) return ApiDefaults.Error(422, "Nombre de 1–100 caracteres, precio entre 1 y 1000000000 centavos y stock >= 0");
            var product = store.Create(input);
            return Results.Created($"/api/v1/products/{product.Id}", product);
        }).Produces<Product>(201);
        app.MapPut("/api/v1/products/{id:long}", (long id, ProductInput input, InventoryStore store) =>
            !ApiDefaults.Valid(input) ? ApiDefaults.Error(422, "Producto inválido") : Results.Ok(store.Update(id, input))).Produces<Product>();
        app.MapDelete("/api/v1/products/{id:long}", (long id, InventoryStore store) =>
        { store.Delete(id); return Results.NoContent(); }).Produces(204);
        app.MapGet("/api/v1/articles", (InventoryStore store, int offset = 0, int limit = 100) =>
            ApiDefaults.ValidPage(offset, limit) ? Results.Ok(store.List(offset, limit).Select(ToArticle)) : ApiDefaults.Error(422, "Paginación inválida"));
        app.MapGet("/api/v1/articles/{id:long}", (long id, InventoryStore store) =>
            store.Get(id) is { } product ? Results.Ok(ToArticle(product)) : ApiDefaults.Error(404, "Artículo no encontrado"));
        app.MapPost("/api/v1/articles", (ArticleInput input, InventoryStore store) =>
        {
            if (!ValidArticle(input)) return ApiDefaults.Error(422, "Artículo inválido");
            var product = store.Create(new(input.Description, input.UnitPriceCents, input.Stock ?? 0, input.IsActive));
            return Results.Created($"/api/v1/articles/{product.Id}", ToArticle(product));
        });
        app.MapPut("/api/v1/articles/{id:long}", (long id, ArticleInput input, InventoryStore store) =>
            ValidArticle(input) ? Results.Ok(ToArticle(store.UpdateArticle(id, input))) : ApiDefaults.Error(422, "Artículo inválido"));
        app.MapDelete("/api/v1/articles/{id:long}", (long id, InventoryStore store) =>
        { store.Delete(id); return Results.NoContent(); });
        app.MapPost("/internal/reservations", (ReservationInput input, InventoryStore store) =>
        {
            if (string.IsNullOrWhiteSpace(input.OperationId) || input.OperationId.Length > 100 || input.ProductId <= 0 || input.Quantity <= 0)
                return ApiDefaults.Error(422, "Reserva inválida");
            return Results.Ok(store.Reserve(input));
        }).Produces<Reservation>();
        app.MapPost("/internal/reservations/{operationId}/confirm", (string operationId, InventoryStore store) =>
            Results.Ok(store.Confirm(operationId))).Produces<Reservation>();
        app.MapPost("/internal/reservations/{operationId}/cancel", (string operationId, InventoryStore store) =>
            Results.Ok(store.Cancel(operationId))).Produces<Reservation>();
        return app;
    }
    private static Article ToArticle(Product product) => new(product.Id, product.Name, product.PriceCents, product.IsActive, product.Stock);
    private static bool ValidArticle(ArticleInput input) => !string.IsNullOrWhiteSpace(input.Description)
        && input.Description.Trim().Length <= 100 && input.UnitPriceCents is > 0 and <= 1_000_000_000 && (input.Stock is null or >= 0);
}
