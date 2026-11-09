namespace Dprental.RentalService.Application.DTOs.Settlement;

/// <summary>Mức hư hại supplier chọn cho một máy; số tiền do hệ thống tính.</summary>
public sealed record DamageAssessment(Guid AssetId, int Level);

public sealed record SubmitDeductionRequest(
    IReadOnlyList<DamageAssessment> Assessments,
    string DamageDescription,
    IReadOnlyList<Guid> EvidenceMediaIds);

public sealed record SubmitDeductionResponse(
    Guid ProposalId,
    decimal DeductionAmount,
    DateTimeOffset SubmittedAt,
    DateTimeOffset RenterResponseDeadline,
    Guid RentalItemId,
    string ItemStatus);
