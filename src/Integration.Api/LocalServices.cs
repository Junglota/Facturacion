using System.Diagnostics;
using System.Net.Http.Json;
using System.Net;
using System.Net.Sockets;

namespace Integration.Api;

// Arranque local con un solo comando; cada servicio conserva su propio proceso.
public sealed class LocalServices(IConfiguration configuration, IWebHostEnvironment environment, ILogger<LocalServices> logger) : IHostedService, IDisposable
{
    private readonly List<Process> children = [];
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("START_LOCAL_SERVICES", true)) return;
        try
        {
            using var supervisor = Process.GetCurrentProcess();
            var supervisorStart = supervisor.StartTime.ToUniversalTime().Ticks;
            foreach (var (name, service, port) in new[] { ("Inventory", "inventory", 62711), ("Sales", "sales", 62712) })
            {
                var url = configuration[$"{name}:Url"] ?? $"http://127.0.0.1:{port}";
                var uri = new Uri(url);
                if (!uri.IsLoopback || uri.Scheme != "http")
                    throw new InvalidOperationException("El arranque local requiere HTTP de loopback. Para servicios remotos use START_LOCAL_SERVICES=false.");
                var probe = new TcpListener(uri.HostNameType == UriHostNameType.IPv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, uri.Port);
                try { probe.Start(); }
                catch (SocketException error) { throw new InvalidOperationException($"El puerto {uri.Port} de {name} está ocupado. Detenga la otra instancia o configure otro puerto.", error); }
                finally { probe.Stop(); }
                var bundled = Path.Combine(AppContext.BaseDirectory, "services", service, $"{name}.Api.dll");
                var info = new ProcessStartInfo("dotnet") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                if (File.Exists(bundled)) info.ArgumentList.Add(bundled);
                else
                {
                    var project = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", $"{name}.Api", $"{name}.Api.csproj"));
                    if (!File.Exists(project)) throw new FileNotFoundException("Falta el servicio en services/ o su proyecto de desarrollo", project);
                    foreach (var arg in new[] { "run", "--project", project, "--no-build", "--no-launch-profile", "--configuration",
#if DEBUG
                        "Debug",
#else
                        "Release",
#endif
                        "--" }) info.ArgumentList.Add(arg);
                }
                info.ArgumentList.Add("--urls");
                info.ArgumentList.Add(url);
                info.WorkingDirectory = environment.ContentRootPath;
                // Los servicios solo necesitan la clave interna, no las credenciales del gateway.
                info.Environment.Remove("ADMIN_USERNAME");
                info.Environment.Remove("ADMIN_PASSWORD");
                info.Environment.Remove("API_KEY");
                info.Environment.Remove("AUTH_DATABASE_PATH");
                info.Environment["INTEGRATION_SUPERVISOR_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                info.Environment["INTEGRATION_SUPERVISOR_START_UTC_TICKS"] = supervisorStart.ToString(System.Globalization.CultureInfo.InvariantCulture);
                info.Environment["DATABASE_PATH"] = Path.GetFullPath(configuration[$"{name}:DatabasePath"] ?? Path.Combine(environment.ContentRootPath, "data", $"{service}.db"));
                info.Environment["SERVICE_API_KEY"] = configuration["SERVICE_API_KEY"] ?? "local-service-key";
                info.Environment["Inventory__Url"] = configuration["Inventory:Url"] ?? "http://127.0.0.1:62711";
                var process = new Process { StartInfo = info };
                process.OutputDataReceived += (_, entry) => { if (entry.Data is not null) logger.LogInformation("[{Service}] {Line}", name, entry.Data); };
                process.ErrorDataReceived += (_, entry) => { if (entry.Data is not null) logger.LogWarning("[{Service}] {Line}", name, entry.Data); };
                process.Start();
                children.Add(process);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                var ready = false;
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    if (process.HasExited) throw new InvalidOperationException($"{name} terminó al iniciar. Compruebe el puerto {uri.Port} y sus registros.");
                    try
                    {
                        var health = await client.GetFromJsonAsync<Dictionary<string, string>>(url.TrimEnd('/') + "/health", cancellationToken);
                        if (health?.GetValueOrDefault("service") == service) { ready = true; break; }
                    }
                    catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { }
                    await Task.Delay(250, cancellationToken);
                }
                if (!ready) throw new InvalidOperationException($"El servicio {name} no quedó disponible en {url}");
                await Task.Delay(200, cancellationToken);
                if (process.HasExited) throw new InvalidOperationException($"{name} no pudo tomar el puerto {uri.Port}");
            }
        }
        catch { StopChildren(); throw; }
    }
    public Task StopAsync(CancellationToken cancellationToken) { StopChildren(); return Task.CompletedTask; }
    private void StopChildren()
    {
        foreach (var process in children.AsEnumerable().Reverse())
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
        children.Clear();
    }
    public void Dispose() => StopChildren();
}
