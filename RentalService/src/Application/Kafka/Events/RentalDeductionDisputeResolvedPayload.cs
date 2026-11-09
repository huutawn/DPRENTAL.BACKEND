namespace Dprental.RentalService.Application.Kafka.Events;

/// <summary>Final Rental decision; Payment settlement and refund are separate operations.</summary>
public sealed record RentalDeductionDisputeResolvedPayload(
    Guid DisputeId,
    Guid DeductionProposalId,
    Guid RentalOrderId,
    Guid RentalItemId,
    Guid RenterId,
    Guid SupplierId,
    string Decision,
    decimal FinalDeductionAmount,
    DateTimeOffset ResolvedAt,
    long OrderVersion);
