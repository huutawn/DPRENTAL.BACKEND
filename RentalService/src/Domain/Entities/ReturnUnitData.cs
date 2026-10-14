using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Domain.Entities;

/// <summary>Input to the aggregate return operation; independent of HTTP and application DTOs.</summary>
public sealed record ReturnUnitData(Guid AssignmentId, UnitCondition Condition,
    string? Description, List<Guid> MediaIds);
