using Integration.Contracts;

namespace Sales.Api;

public sealed class SaleCoordinator(SalesStore store, InventoryClient inventory, ILogger<SaleCoordinator> logger)
{
    // ponytail: serializa operaciones por proceso; usar particiones por venta si aumenta el tráfico.
    // La corrección entre réplicas depende del estado persistido y las reservas idempotentes.
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<(Sale Sale, bool Replayed)> Create(SaleInput input, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var operation = store.Start(input);
            var replayed = operation.Sale.Status == "Confirmed";
            await Advance(operation.Sale.Id, cancellationToken);
            return (Result(operation.Sale.Id), replayed);
        }
        finally { gate.Release(); }
    }
    public async Task<Sale> Cancel(long id, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var operation = store.Get(id) ?? throw new BusinessException(404, "Venta no encontrada");
            if (operation.Sale.Status is "Pending") store.SetStatus(id, "Cancelling", "Pending");
            operation = store.Get(id)!;
            if (operation.Sale.Status is "Confirmed" or "Confirming")
                throw new BusinessException(409, "La venta ya se confirmó o está confirmándose");
            if (operation.Sale.Status == "Rejected") throw new BusinessException(409, "Una venta rechazada no necesita cancelación");
            await Advance(id, cancellationToken);
            return Result(id);
        }
        finally { gate.Release(); }
    }
    public async Task Recover(CancellationToken cancellationToken)
    {
        foreach (var id in store.Pending())
        {
            await gate.WaitAsync(cancellationToken);
            try { await Advance(id, cancellationToken); }
            catch (BusinessException error) { logger.LogError(error, "La venta {SaleId} requiere revisar su reserva", id); }
            finally { gate.Release(); }
        }
    }
    private Sale Result(long id)
    {
        var operation = store.Get(id)!;
        if (operation.Sale.Status == "Rejected")
            throw new BusinessException(operation.ErrorStatus ?? 409, operation.Sale.Failure ?? "Venta rechazada");
        return operation.Sale;
    }
    private async Task Advance(long id, CancellationToken cancellationToken)
    {
        var operation = store.Get(id)!;
        try
        {
            if (operation.Sale.Status == "Cancelling")
            {
                await inventory.Cancel(operation.OperationId, cancellationToken);
                store.SetStatus(id, "Cancelled", "Cancelling");
                return;
            }
            if (operation.Sale.Status == "Pending")
            {
                Reservation reservation;
                try { reservation = await inventory.Reserve(operation, cancellationToken); }
                catch (BusinessException error)
                {
                    store.SetStatus(id, "Rejected", "Pending", error.Message, error.Status);
                    return;
                }
                store.PrepareConfirmation(id, checked(reservation.UnitPriceCents * operation.Sale.Quantity));
                operation = store.Get(id)!;
            }
            if (operation.Sale.Status == "Confirming")
            {
                // La decisión de confirmar ya quedó persistida antes de la llamada HTTP.
                await inventory.Confirm(operation.OperationId, cancellationToken);
                store.SetStatus(id, "Confirmed", "Confirming");
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            // La respuesta pudo perderse después de aplicar el cambio remoto: conservar el estado y reintentar el MISMO ID.
            logger.LogWarning("Venta {SaleId} pendiente de recuperación: {Reason}", id, error.Message);
        }
    }
}

public sealed class SaleRecovery(SaleCoordinator coordinator, ILogger<SaleRecovery> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                await coordinator.Recover(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "No se pudo completar el ciclo de recuperación"); }
        }
    }
}
