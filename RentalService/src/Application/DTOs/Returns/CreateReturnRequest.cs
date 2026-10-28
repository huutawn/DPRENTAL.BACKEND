using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Application.DTOs.Returns;

public sealed record CreateReturnRequest(DateTimeOffset ActualReturnAt, string? Note, List<ReturnRequestUnitDto>? Units);

public sealed record ReturnRequestUnitDto(Guid RentalUnitAssignmentId, List<Guid>? MediaIds);

public sealed record ReturnRequestUnitResponse(
    Guid RentalUnitAssignmentId,
    IReadOnlyList<Guid> MediaIds,
    DateTimeOffset? ClosedAt = null,
    DateTimeOffset? RejectedAt = null,
    ReturnRequestUnitStatus Status = ReturnRequestUnitStatus.PendingSupplierConfirmation,
    bool IsOverdue = false,
    DateTimeOffset? OverdueNotifiedAt = null)
{
    public Guid AssignmentId => RentalUnitAssignmentId;
}

public sealed record ReturnRequestIssueResponse(
    Guid IssueId,
    Guid ReturnRequestId,
    Guid ReporterId,
    ActorRole ReporterRole,
    string Reason,
    string? Note,
    IReadOnlyList<Guid> MediaIds,
    DateTimeOffset ReportedAt,
    IReadOnlyList<Guid> AssignmentIds);

public sealed record ReturnRequestResolutionResponse(
    Guid ResolutionId,
    Guid ReturnRequestId,
    Guid AdminId,
    ReturnResolutionDecision Decision,
    string? DecisionNote,
    IReadOnlyList<Guid> ResolutionMediaIds,
    DateTimeOffset ResolvedAt,
    IReadOnlyList<Guid> AssignmentIds);

public sealed record ReturnRequestResponse(
    Guid ReturnRequestId,
    Guid ItemId,
    DateTimeOffset ActualReturnAt,
    string? Note,
    DateTimeOffset SubmittedAt,
    IReadOnlyList<ReturnRequestUnitResponse> Units,
    ReturnRequestStatus Status = ReturnRequestStatus.PendingSupplierConfirmation,
    DateTimeOffset ConfirmationDeadlineAt = default,
    bool IsOverdue = false,
    IReadOnlyList<ReturnRequestIssueResponse>? Issues = null,
    IReadOnlyList<ReturnRequestResolutionResponse>? Resolutions = null);
