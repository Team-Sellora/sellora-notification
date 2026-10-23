namespace Sellora.NotificationService.Application.Rendering;

// The full order-event payload as sellora-order publishes it
// (Application/Events/OrderEvents.cs, docs/contracts/order-events.v1.md),
// read only to compose the message. Names must match: System.Text.Json
// binds by name (case-insensitive).

public sealed record OrderEventDetails(
    string? EventType,
    Guid OrderId,
    string? OrderReference,
    DateTimeOffset OccurredAt,
    string? FulfilmentType,
    DateTimeOffset OrderDate,
    DetailsShop? Shop,
    DetailsAgency? Agency,
    DetailsSalesRep? SalesRep,
    IReadOnlyList<DetailsLine>? Lines,
    decimal Subtotal,
    decimal Total,
    string? Currency,
    DetailsPayment? Payment,
    DetailsLocation? CheckInLocation,
    DetailsLocation? CheckoutLocation,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? CancelledAt,
    string? Reason);

public sealed record DetailsShop(Guid ShopId, string? Name, string? OwnerName);

public sealed record DetailsAgency(Guid AgencyId, string? Name);

public sealed record DetailsSalesRep(Guid SalesRepId, string? Name);

public sealed record DetailsLine(Guid ProductId, string? ProductName, int Quantity, decimal UnitPrice, decimal LineTotal);

public sealed record DetailsPayment(Guid PaymentId, decimal Amount, string? Method, DateTimeOffset RecordedAt);

public sealed record DetailsLocation(
    double Latitude,
    double Longitude,
    double DistanceMeters,
    double? AccuracyMeters,
    DateTimeOffset CheckedInAt);
