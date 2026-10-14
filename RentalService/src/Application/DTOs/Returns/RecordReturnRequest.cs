using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Application.DTOs.Returns;

public sealed record RecordReturnRequest(DateTimeOffset ActualReturnAt, string? Note, List<ReturnUnitRequest> Units);

public sealed record ReturnUnitRequest(Guid RentalUnitAssignmentId, UnitCondition Condition, string? Description, List<Guid> MediaIds);
