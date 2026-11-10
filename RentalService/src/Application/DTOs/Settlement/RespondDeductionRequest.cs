namespace Dprental.RentalService.Application.DTOs.Settlement;

public enum RenterDeductionDecision { Accept = 0, Dispute = 1 }

public sealed record RespondDeductionRequest(
    RenterDeductionDecision Decision,
    string? DisputeReason,
    IReadOnlyList<Guid>? EvidenceMediaIds);

public sealed record RespondDeductionResponse(
    Guid DeductionProposalId,
    Guid RentalItemId,
    string ProposalStatus,
    decimal? FinalDeductionAmount,
    DateTimeOffset RespondedAt,
    string ItemStatus);

public sealed record OfferDisputeAmountRequest(decimal Amount);

public sealed record OfferDisputeAmountResponse(Guid RentalItemId, decimal Amount,
    DateTimeOffset OfferedAt, DateTimeOffset AgreementDeadline);

public sealed record DisputeUnitEvidenceDto(
    Guid AssignmentId, Guid AssetId, string Status,
    string? HandoverCondition, string? HandoverNote, IReadOnlyList<Guid> HandoverMediaIds,
    string? ReturnCondition, string? ReturnNote, IReadOnlyList<Guid> ReturnMediaIds,
    bool IsAffected, string? DeductionReason, IReadOnlyList<Guid> DeductionMediaIds)
{
    public IReadOnlyList<Guid> RenterReturnMediaIds { get; init; } = [];
}

public sealed record DisputeDetailDto(
    Guid RentalItemId, Guid RentalOrderId, string OrderCode, Guid SupplierId, Guid RenterId,
    decimal DepositAmount, decimal TotalProposedAmount, string DamageDescription,
    DateTimeOffset ProposalSubmittedAt, string ProposalStatus,
    string DisputeReason, IReadOnlyList<Guid> DisputeMediaIds, DateTimeOffset? DisputedAt,
    IReadOnlyList<DisputeUnitEvidenceDto> Units)
{
    public decimal? ClaimedDamageAmount { get; init; }
    public decimal? AgreedDeductionAmount { get; init; }
    public decimal? CurrentSupplierOfferAmount { get; init; }
    public decimal? DepositRefundAmount { get; init; }
    public string? FinalizationReason { get; init; }
}

public sealed record DisputeSummaryDto(
    Guid RentalItemId, Guid RentalOrderId, string OrderCode,
    decimal TotalProposedAmount, decimal DepositAmount, DateTimeOffset? DisputedAt)
{
    public decimal? ClaimedDamageAmount { get; init; }
    public decimal? AgreedDeductionAmount { get; init; }
    public decimal? CurrentSupplierOfferAmount { get; init; }
}
