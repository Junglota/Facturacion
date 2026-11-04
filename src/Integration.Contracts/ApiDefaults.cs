using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Integration.Contracts;

public static class ApiDefaults
{
    public static void AddApiDefaults(this WebApplicationBuilder builder, string title, bool allowSession = false)
    {
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo { Title = title, Version = "v1" });
            options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme {
                Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-API-Key" });
            options.AddSecurityRequirement(new OpenApiSecurityRequirement {
                [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" } }] = Array.Empty<string>() });
            if (allowSession)
            {
                options.AddSecurityDefinition("Session", new OpenApiSecurityScheme {
                    Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Cookie, Name = "Facturacion.Session" });
                options.AddSecurityRequirement(new OpenApiSecurityRequirement {
                    [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Session" } }] = Array.Empty<string>() });
            }
            options.OperationFilter<CommonErrors>();
        });
    }

    public static void UseApiDefaults(this WebApplication app, string? key, bool allowSession = false)
    {
        app.Use(async (context, next) =>
        {
            try
            {
                var publicAuth = allowSession && (context.Request.Path.Equals("/api/auth/status") ||
                    context.Request.Path.Equals("/api/auth/setup") || context.Request.Path.Equals("/api/auth/login"));
                if (!publicAuth && (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/internal")))
                {
                    var supplied = context.Request.Headers["X-API-Key"].ToString();
                    var validKey = !string.IsNullOrEmpty(key) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(key));
                    if (!validKey && !(allowSession && context.User.Identity?.IsAuthenticated == true))
                    {
                        await Error(401, allowSession ? "Inicie sesión o proporcione una clave de integración válida" : "Clave de API inválida o ausente").ExecuteAsync(context);
                        return;
                    }
                }
                await next(context);
                if (context.Response.StatusCode == 404 && !context.Response.HasStarted)
                    await Error(404, "Recurso o ruta no encontrado").ExecuteAsync(context);
            }
            catch (BusinessException exception) { await Error(exception.Status, exception.Message).ExecuteAsync(context); }
            catch (BadHttpRequestException) { await Error(400, "JSON, ruta o parámetros inválidos").ExecuteAsync(context); }
        });
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    public static IResult Error(int status, string message) => Results.Json(
        new ErrorResponse(new(status switch {
            400 => "invalid_request", 401 => "unauthorized", 404 => "not_found",
            409 => "conflict", 422 => "validation_error", 503 => "service_unavailable", _ => "http_error"
        }, message)), statusCode: status);

    public static bool Valid(ProductInput input) => !string.IsNullOrWhiteSpace(input.Name)
        && input.Name.Trim().Length <= 100 && input.PriceCents is > 0 and <= 1_000_000_000 && input.Stock >= 0;
    public static bool Valid(SaleInput input) => input.ProductId > 0 && input.Quantity > 0
        && !string.IsNullOrWhiteSpace(input.Reference) && input.Reference.Trim().Length <= 100;
    public static bool ValidPage(int offset, int limit) => offset >= 0 && limit is >= 1 and <= 100;
}

public sealed class CommonErrors : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.RelativePath is "health" or "api/auth/status" or "api/auth/setup" or "api/auth/login")
        {
            // Un requisito vacío deja explícito que la operación admite acceso anónimo.
            operation.Security = new List<OpenApiSecurityRequirement> { new() };
            return;
        }
        if (context.ApiDescription.RelativePath?.StartsWith("api/auth/", StringComparison.Ordinal) == true)
            operation.Security = new List<OpenApiSecurityRequirement> { new() {
                [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Session" } }] = Array.Empty<string>()
            } };
        foreach (var code in new[] { 400, 401, 404, 409, 422, 503 })
            operation.Responses.TryAdd(code.ToString(), new OpenApiResponse {
                Description = "Error de la solicitud o del servicio",
                Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() {
                    Schema = context.SchemaGenerator.GenerateSchema(typeof(ErrorResponse), context.SchemaRepository)
                } }
            });
    }
}
