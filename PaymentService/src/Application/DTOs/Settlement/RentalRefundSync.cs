namespace Dprental.PaymentService.Application.DTOs.Settlement;

// Internal Rental -> Payment contract. Public FE DTOs are unchanged.
public sealed record RentalRefundSyncRequest(
    Guid RentalOrderId, Guid RentalItemId, string SourceOperationKey, decimal Amount,
    string? BankTransactionRef, Guid? ConfirmedBy, DateTimeOffset? ConfirmedAt, string? Note);

public sealed record RentalRefundSyncResponse(
    bool Completed, decimal Amount, string? BankTransactionRef,
    Guid? ConfirmedBy, DateTimeOffset? ConfirmedAt, bool ConfirmationApplied = false);
