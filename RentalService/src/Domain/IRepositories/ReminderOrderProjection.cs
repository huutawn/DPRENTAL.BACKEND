using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Domain.IRepositories;

public sealed record ReminderOrderProjection(
    Guid OrderId,
    RentalOrderStatus OrderStatus,
    string OrderCode,
    Guid RenterId,
    long OrderVersion,
    IReadOnlyList<ReminderItemProjection> Items);

public sealed record ReminderItemProjection(
    Guid ItemId,
    Guid SupplierId,
    RentalItemStatus Status,
    DateOnly ExpectedStartDate,
    DateOnly ExpectedEndDate,
    int TotalQuantity,
    bool HandoverInProgress,
    IReadOnlyList<Guid> AllAssignmentIds,
    IReadOnlyList<Guid> ReturnedAssignmentIds,
    IReadOnlyList<Guid> ActiveReturnRequestedAssignmentIds);
