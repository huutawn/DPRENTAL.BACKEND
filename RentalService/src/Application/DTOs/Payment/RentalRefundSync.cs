namespace Dprental.RentalService.Application.DTOs.Payment;

// Internal service contract only; public Rental/Payment DTOs remain unchanged.
public sealed record RentalRefundSyncRequest(
    Guid RentalOrderId, Guid RentalItemId, string SourceOperationKey, decimal Amount,
    string? BankTransactionRef, Guid? ConfirmedBy, DateTimeOffset? ConfirmedAt, string? Note);

public sealed record RentalRefundSyncResponse(
    bool Completed, decimal Amount, string? BankTransactionRef,
    Guid? ConfirmedBy, DateTimeOffset? ConfirmedAt, bool ConfirmationApplied = false);
