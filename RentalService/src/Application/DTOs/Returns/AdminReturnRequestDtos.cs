using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Application.DTOs.Returns;

public sealed record AdminResolveReturnRequest(
    ReturnResolutionDecision Decision,
    string DecisionNote,
    List<Guid> AssignmentIds,
    List<Guid>? ResolutionMediaIds = null,
    DateTimeOffset? ActualReturnAt = null,
    List<ReturnUnitRequest>? Units = null);

public sealed record AdminReturnRequestQuery(
    int Page = 1,
    int PageSize = 20,
    bool? Disputed = null,
    bool? Overdue = null,
    Guid? ItemId = null,
    Guid? OrderId = null);

public sealed record AdminReturnRequestSummaryResponse(
    Guid ReturnRequestId,
    Guid ItemId,
    Guid OrderId,
    string OrderCode,
    Guid RenterId,
    Guid SupplierId,
    DateTimeOffset SubmittedAt,
    DateTimeOffset ConfirmationDeadlineAt,
    ReturnRequestStatus Status,
    bool IsOverdue,
    int TotalUnits,
    int ActionableUnits,
    int DisputedUnits,
    int ConfirmedUnits,
    int RejectedUnits);

public sealed record AdminReturnUnitInventoryStatusResponse(
    Guid RentalUnitAssignmentId,
    Guid AssetId,
    Guid AllocationId,
    RentalUnitAssignmentStatus AssignmentStatus,
    Guid? ReturnRecordId,
    UnitCondition? ReturnCondition,
    DateTimeOffset? AllocationClosedAt,
    string? InventoryDispositionRef);
public sealed record AdminReturnRequestDetailResponse(
    ReturnRequestResponse Request,
    Guid OrderId,
    string OrderCode,
    Guid SupplierId,
    Guid RenterId,
    RentalItemStatus ItemStatus,
    IReadOnlyList<AdminReturnUnitInventoryStatusResponse> InventoryUnits);
