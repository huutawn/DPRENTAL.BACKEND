namespace Dprental.RentalService.Application.DTOs.Settlement;

public sealed record SubmitDeductionRequest(
    decimal DeductionAmount,
    string DamageDescription,
    IReadOnlyList<Guid> EvidenceMediaIds,
    IReadOnlyList<Guid> AffectedAssetIds);

public sealed record SubmitDeductionResponse(
    Guid ProposalId,
    decimal DeductionAmount,
    DateTimeOffset SubmittedAt,
    DateTimeOffset RenterResponseDeadline,
    Guid RentalItemId,
    string ItemStatus);
