using System.Text.Json.Serialization;

namespace Integration.Contracts;

public record ProductInput([property: JsonRequired] string Name, [property: JsonRequired] long PriceCents, [property: JsonRequired] int Stock, bool? IsActive = null);
public record Product(long Id, string Name, long PriceCents, int Stock, bool IsActive = true);
public record ArticleInput([property: JsonRequired] string Description, [property: JsonRequired] long UnitPriceCents, [property: JsonRequired] bool IsActive, int? Stock = null);
public record Article(long Id, string Description, long UnitPriceCents, bool IsActive, int Stock);
public record ClientInput([property: JsonRequired] string Name, [property: JsonRequired] string TaxId, [property: JsonRequired] string AccountingAccount, [property: JsonRequired] bool IsActive);
public record Client(long Id, string Name, string TaxId, string AccountingAccount, bool IsActive);
public record SellerInput([property: JsonRequired] string Name, [property: JsonRequired] decimal CommissionPercent, [property: JsonRequired] bool IsActive);
public record Seller(long Id, string Name, decimal CommissionPercent, bool IsActive);
public record Credentials([property: JsonRequired] string Username, [property: JsonRequired] string Password);
public record SaleInput([property: JsonRequired] long ProductId, [property: JsonRequired] int Quantity, [property: JsonRequired] string Reference);
public record Sale(long Id, long ProductId, int Quantity, string Reference, long? TotalCents, string Status, string? Failure = null);
public record ReservationInput([property: JsonRequired] string OperationId, [property: JsonRequired] long ProductId, [property: JsonRequired] int Quantity);
public record Reservation(string OperationId, long? ProductId, int Quantity, long UnitPriceCents, string Status);
public record ApiError(string Code, string Message);
public record ErrorResponse(ApiError Error);

public sealed class BusinessException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
