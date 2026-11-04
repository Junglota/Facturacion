using System.Net.Http.Json;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Integration.Contracts;
using Integration.Data;

namespace Integration.Api;

public static class GatewayHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddApiDefaults("Gateway de Inventario y Ventas", allowSession: true);
        builder.Services.AddSingleton(services => new UserStore(services.GetRequiredService<IConfiguration>()["AUTH_DATABASE_PATH"] ?? Path.Combine(builder.Environment.ContentRootPath, "data", "users.db")));
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.Cookie.Name = "Facturacion.Session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
        });
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions {
                    PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                }));
        });
        builder.Services.AddCors(options => options.AddPolicy("WebClient", policy => policy
            .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
            .WithHeaders("Content-Type", "X-API-Key", "X-Requested-With").WithMethods("GET", "POST", "PUT", "DELETE").WithExposedHeaders("Location").AllowCredentials()));
        foreach (var service in new[] { "Inventory", "Sales" })
        {
            builder.Services.AddHttpClient(service, (services, client) =>
            {
                var config = services.GetRequiredService<IConfiguration>();
                client.BaseAddress = new Uri(config[$"{service}:Url"] ?? (service == "Inventory" ? "http://127.0.0.1:62711" : "http://127.0.0.1:62712"));
                client.DefaultRequestHeaders.Add("X-API-Key", config["SERVICE_API_KEY"] ?? "local-service-key");
                client.Timeout = TimeSpan.FromSeconds(10);
            });
        }
        builder.Services.AddHostedService<LocalServices>();
        configure?.Invoke(builder);
        if (builder.Configuration.GetValue("START_LOCAL_SERVICES", true) && string.IsNullOrEmpty(builder.Configuration["SERVICE_API_KEY"]))
            builder.Configuration["SERVICE_API_KEY"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var app = builder.Build();
        var userStore = app.Services.GetRequiredService<UserStore>();
        if (!userStore.Initialized() && builder.Configuration["ADMIN_USERNAME"] is { Length: > 0 } username && builder.Configuration["ADMIN_PASSWORD"] is { Length: > 0 } password)
            userStore.Initialize(new(username, password));
        app.UseCors("WebClient");
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        SqliteDb.UseErrors(app);
        app.Use(async (context, next) =>
        {
            var unsafeMethod = context.Request.Method is "POST" or "PUT" or "DELETE" or "PATCH";
            var origin = context.Request.Headers.Origin.ToString();
            var sameOrigin = origin.Equals($"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase);
            if (unsafeMethod && (context.Request.Path.StartsWithSegments("/api/auth") || context.User.Identity?.IsAuthenticated == true)
                && context.Request.Headers["X-Requested-With"] != "XMLHttpRequest" && !sameOrigin)
            {
                await ApiDefaults.Error(403, "Solicitud no permitida").ExecuteAsync(context);
                return;
            }
            await next(context);
        });
        app.UseApiDefaults(builder.Configuration["API_KEY"], allowSession: true);
        app.UseAuthorization();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapGet("/api/auth/status", (UserStore users) => Results.Ok(new { initialized = users.Initialized() }));
        app.MapPost("/api/auth/setup", (Credentials input, UserStore users, HttpContext context) =>
        {
            var host = context.Request.Host.Host.Trim('[', ']');
            var localHost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                (IPAddress.TryParse(host, out var hostIp) && IPAddress.IsLoopback(hostIp));
            if ((!app.Environment.IsDevelopment() && !builder.Configuration.GetValue("ALLOW_LOCAL_SETUP", false)) ||
                !localHost || context.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip))
                return ApiDefaults.Error(403, "Configure el usuario inicial en el equipo local de desarrollo o con ADMIN_USERNAME y ADMIN_PASSWORD");
            users.Initialize(input);
            return Results.NoContent();
        }).RequireRateLimiting("login");
        app.MapPost("/api/auth/login", async (Credentials input, UserStore users, HttpContext context) =>
        {
            var user = users.Authenticate(input);
            if (user is null) return ApiDefaults.Error(401, "Usuario o contraseña incorrectos");
            var claims = new List<Claim> { new(ClaimTypes.Name, user.Username) };
            if (user.IsAdmin) claims.Add(new(ClaimTypes.Role, "Administrator"));
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.Ok(user);
        }).RequireRateLimiting("login");
        app.MapGet("/api/auth/me", (HttpContext context) => context.User.Identity?.IsAuthenticated == true
            ? Results.Ok(new { username = context.User.Identity.Name, isAdmin = context.User.IsInRole("Administrator") }) : ApiDefaults.Error(401, "Inicie sesión"));
        app.MapPost("/api/auth/users", (Credentials input, UserStore users, HttpContext context) =>
        {
            if (!context.User.IsInRole("Administrator")) return ApiDefaults.Error(403, "Solo el usuario inicial puede crear cuentas");
            users.Add(input);
            return Results.Json(new { username = input.Username.Trim(), isAdmin = false }, statusCode: 201);
        });
        app.MapPost("/api/auth/logout", async (HttpContext context) =>
        { await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); return Results.NoContent(); });
        app.MapGet("/health", async (IHttpClientFactory clients) =>
        {
            var inventory = await Healthy(clients, "Inventory");
            var sales = await Healthy(clients, "Sales");
            return Results.Json(new { status = inventory && sales ? "ok" : "degraded", service = "gateway", inventory, sales },
                statusCode: inventory && sales ? 200 : 503);
        });
        app.MapGet("/api/v1/products", (HttpContext context, IHttpClientFactory clients, int offset = 0, int limit = 100) => Forward(context, clients, "Inventory"))
            .Produces<List<Product>>();
        app.MapGet("/api/v1/products/{id:long}", (long id, HttpContext context, IHttpClientFactory clients) => Forward(context, clients, "Inventory"))
            .Produces<Product>();
        app.MapPost("/api/v1/products", (ProductInput input, HttpContext context, IHttpClientFactory clients) => Forward(context, clients, "Inventory", input))
            .Produces<Product>(201);
        app.MapPut("/api/v1/products/{id:long}", (long id, ProductInput input, HttpContext context, IHttpClientFactory clients) => Forward(context, clients, "Inventory", input))
            .Produces<Product>();
        app.MapDelete("/api/v1/products/{id:long}", (long id, HttpContext context, IHttpClientFactory clients) => Forward(context, clients, "Inventory"))
            .Produces(204);
        app.MapGet("/api/v1/articles", (HttpContext context, IHttpClientFactory clients, int offset = 0, int limit = 100) => Forward(context, clients, "Inventory")).Produces<List<Article>>();
        app.MapGet("/api/v1/articles/{id:long}", (long id, HttpContext context, IHttpClientFactory clients) => Forward(context, clients, "Inventory")).Produces<Article>();
        app.MapPost("/api/v1/articles", (ArticleInput input, HttpContext context, IHttpClientFactory clients) => Forward(context, clients, "Inventory", input)).Produces<Article>(201);
        app.MapPut("/api/v1/articles/{id:long}", (long id, ArticleInput input, HttpContext context, IHttpClientFactory clients) => Forward(context, clients, "Inventory", input)).Produces<Article>();
        app.MapDelete("/api/v1/articles/{id:long}", (long id, HttpContext context, IHttpClientFactory clients) => Forward(context, clients, "Inventory")).Produces(204);
        app.MapGet("/api/v1/clients", (HttpContext context, IHttpClientFactory clients, int offset = 0, int limit = 100) => Forward(context, clients, "Sales")).Produces<List<Client>>();
        app.MapGet("/api/v1/clients/{id:long}", (long id, HttpContext context, IHttpClientFactory clients) => Forward(context, clients, "Sales")).Produces<Client>();
        app.MapPost("/api/v1/clients", (ClientInput input, HttpContext context, IHttpClientFactory clients) => Forward(context, clients, "Sales", input)).Produces<Client>(201);
        app.MapPut("/api/v1/clients/{id:long}", (long id, ClientInput input, HttpContext context, IHttpClientFactory clients) => Forward(context, clients, "Sales", input)).Produces<Client>();
        app.MapDelete("/api/v1/clients/{id:long}", (long id, HttpContext context, IHttpClientFactory clients) => Forward(context, clients, "Sales")).Produces(204);
        app.MapGet("/api/v1/sellers", (HttpContext context, IHttpClientFactory clients, int offset = 0, int limit = 100) => Forward(context, clients, "Sales")).Produces<List<Seller>>();
        app.MapGet("/api/v1/sellers/{id:long}", (long id, HttpContext context, IHttpClientFactory clients) => Forward(context, clients, "Sales")).Produces<Seller>();
        app.MapPost("/api/v1/sellers", (SellerInput input, HttpContext context, IHttpClientFactory clients) => Forward(context, clients, "Sales", input)).Produces<Seller>(201);
        app.MapPut("/api/v1/sellers/{id:long}", (long id, SellerInput input, HttpContext context, IHttpClientFactory clients) => Forward(context, clients, "Sales", input)).Produces<Seller>();
        app.MapDelete("/api/v1/sellers/{id:long}", (long id, HttpContext context, IHttpClientFactory clients) => Forward(context, clients, "Sales")).Produces(204);
        app.MapGet("/api/v1/sales", (HttpContext context, IHttpClientFactory clients, int offset = 0, int limit = 100) => Forward(context, clients, "Sales"))
            .Produces<List<Sale>>();
        app.MapGet("/api/v1/sales/{id:long}", (long id, HttpContext context, IHttpClientFactory clients) => Forward(context, clients, "Sales"))
            .Produces<Sale>();
        app.MapPost("/api/v1/sales", (SaleInput input, HttpContext context, IHttpClientFactory clients) => Forward(context, clients, "Sales", input))
            .Produces<Sale>(201).Produces<Sale>(200).Produces<Sale>(202);
        app.MapPost("/api/v1/sales/{id:long}/cancel", (long id, HttpContext context, IHttpClientFactory clients) => Forward(context, clients, "Sales"))
            .Produces<Sale>().Produces<Sale>(202);
        return app;
    }
    private static async Task<bool> Healthy(IHttpClientFactory clients, string name)
    {
        try
        {
            using var client = clients.CreateClient(name);
            client.Timeout = TimeSpan.FromSeconds(2);
            using var response = await client.GetAsync("/health");
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { return false; }
    }
    private static async Task Forward(HttpContext context, IHttpClientFactory clients, string service, object? body = null)
    {
        try
        {
            using var client = clients.CreateClient(service);
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), context.Request.Path + context.Request.QueryString);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } type) context.Response.ContentType = type.ToString();
            if (response.Headers.Location is { } location) context.Response.Headers.Location = location.ToString();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (Exception error) when ((error is HttpRequestException or OperationCanceledException) && !context.RequestAborted.IsCancellationRequested)
        {
            await ApiDefaults.Error(503, $"El servicio {service} no está disponible").ExecuteAsync(context);
        }
    }
}
