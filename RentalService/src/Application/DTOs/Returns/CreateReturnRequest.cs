namespace Dprental.RentalService.Application.DTOs.Returns;

public sealed record CreateReturnRequest(DateTimeOffset ActualReturnAt, string? Note, List<ReturnRequestUnitDto>? Units);

public sealed record ReturnRequestUnitDto(Guid RentalUnitAssignmentId, List<Guid>? MediaIds);

public sealed record CreateReturnRequestResponse(Guid ReturnRequestId, Guid ItemId,
    DateTimeOffset ActualReturnAt, string? Note, DateTimeOffset SubmittedAt, IReadOnlyList<ReturnRequestUnitDto> Units);
