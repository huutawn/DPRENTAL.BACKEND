using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Domain.Entities;

/// <summary>Bằng chứng bàn giao của một máy; độc lập với HTTP và DTO của Application.</summary>
public sealed record HandoverUnitData(Guid AssignmentId, UnitCondition Condition,
    string? Note, IReadOnlyList<Guid> MediaIds);
