using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Integration.Contracts;

// Solo se activa para servicios iniciados por el gateway local.
public sealed class SupervisorMonitor(IHostApplicationLifetime lifetime, ILogger<SupervisorMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var value = Environment.GetEnvironmentVariable("INTEGRATION_SUPERVISOR_PID");
        if (string.IsNullOrEmpty(value)) return;
        var start = Environment.GetEnvironmentVariable("INTEGRATION_SUPERVISOR_START_UTC_TICKS");
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0 ||
            !long.TryParse(start, NumberStyles.None, CultureInfo.InvariantCulture, out var startTicks))
        {
            logger.LogError("La identificación del gateway local no es válida.");
            lifetime.StopApplication();
            return;
        }
        try
        {
            using var supervisor = Process.GetProcessById(pid);
            // Tolerancia pequeña para diferencias al leer el reloj de inicio en distintas plataformas.
            var difference = Math.Abs(supervisor.StartTime.ToUniversalTime().Ticks - (double)startTicks);
            if (difference > TimeSpan.FromSeconds(2).Ticks)
            {
                logger.LogWarning("El proceso del gateway ya no corresponde a esta ejecución.");
                lifetime.StopApplication();
                return;
            }
            await supervisor.WaitForExitAsync(stoppingToken);
            if (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("El gateway terminó; cerrando este servicio local.");
                lifetime.StopApplication();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(error, "No se puede vigilar el gateway local; cerrando este servicio.");
            lifetime.StopApplication();
        }
    }
}
