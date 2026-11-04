using System.Net.Http.Json;
using Integration.Contracts;

namespace Sales.Api;

public sealed class InventoryClient(IHttpClientFactory factory)
{
    public const string ClientName = "inventory";
    public async Task<Reservation> Reserve(SaleOperation operation, CancellationToken cancellationToken)
    {
        using var client = factory.CreateClient(ClientName);
        using var response = await client.PostAsJsonAsync("/internal/reservations", new ReservationInput(
            operation.OperationId, operation.Sale.ProductId, operation.Sale.Quantity), cancellationToken);
        return await Read(response, cancellationToken);
    }
    public async Task Confirm(string operationId, CancellationToken cancellationToken)
    {
        using var client = factory.CreateClient(ClientName);
        using var response = await client.PostAsync($"/internal/reservations/{operationId}/confirm", null, cancellationToken);
        await Read(response, cancellationToken);
    }
    public async Task Cancel(string operationId, CancellationToken cancellationToken)
    {
        using var client = factory.CreateClient(ClientName);
        using var response = await client.PostAsync($"/internal/reservations/{operationId}/cancel", null, cancellationToken);
        await Read(response, cancellationToken);
    }
    private static async Task<Reservation> Read(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode is 404 or 409 or 422)
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(cancellationToken);
            throw new BusinessException((int)response.StatusCode, error?.Error.Message ?? "Reserva rechazada");
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Reservation>(cancellationToken)
            ?? throw new HttpRequestException("Inventario devolvió una respuesta vacía");
    }
}
