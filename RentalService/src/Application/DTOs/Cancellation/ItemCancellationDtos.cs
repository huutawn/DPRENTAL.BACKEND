namespace Dprental.RentalService.Application.DTOs.Cancellation;

public sealed record CancelNotDeliveredRequest(
    string? Reason);

public sealed record CancelNotDeliveredResponse(
    Guid ItemId,
    string Status,
    DateTimeOffset CancelledAt,
    decimal RefundAmount);
