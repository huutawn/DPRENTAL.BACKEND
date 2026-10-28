namespace Dprental.RentalService.Application.DTOs.Returns;

public sealed record CreateReturnRequest(DateTimeOffset ActualReturnAt, string? Note, List<ReturnRequestUnitDto>? Units);

public sealed record ReturnRequestUnitDto(Guid RentalUnitAssignmentId, List<Guid>? MediaIds);

public sealed record ReturnRequestUnitResponse(
    Guid RentalUnitAssignmentId,
    IReadOnlyList<Guid> MediaIds,
    DateTimeOffset? ClosedAt = null)
{
    public Guid AssignmentId => RentalUnitAssignmentId;
}

public sealed record ReturnRequestResponse(
    Guid ReturnRequestId,
    Guid ItemId,
    DateTimeOffset ActualReturnAt,
    string? Note,
    DateTimeOffset SubmittedAt,
    IReadOnlyList<ReturnRequestUnitResponse> Units);
