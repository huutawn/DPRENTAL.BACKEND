using System;
using System.Collections.Generic;
using System.Text;

namespace Dprental.PaymentService.Application.DTOs.Payout
{
    public sealed record SupplierPayoutListItem(
    Guid PayoutId, string PayoutRef, Guid RentalItemId, string SettlementRef,
    decimal Amount, string Currency, string Status, string? ExternalTransferReference,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

    public sealed record AdminPayoutListItem(
        Guid PayoutId, string PayoutRef, Guid SettlementId, string SettlementRef, Guid RentalItemId, Guid SupplierId,
        decimal Amount, string Currency, string Status, string? DestinationRef, string? ExternalTransferReference,
        Guid? CompletedBy, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
        Guid? DestinationId, int? DestinationVersion);
}
