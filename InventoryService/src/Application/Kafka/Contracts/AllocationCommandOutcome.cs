namespace Dprental.InventoryService.Application.Kafka.Contracts;

/// <summary>
/// Đại diện cho kết quả xử lý nghiệp vụ của một allocation command.
/// Tách bạch giữa outcome nghiệp vụ (Succeeded hoặc Rejected) và lỗi hạ tầng (Result.Failure).
/// Hỗ trợ cả 5 command phân bổ tiêu chuẩn và Return (ApplyReturnedUnits).
/// </summary>
public sealed record AllocationCommandOutcome(
    bool IsSuccess,
    string CommandType,
    string OperationKey,
    Guid RentalOrderId,
    Guid RentalItemId,
    InventoryAllocationCommandSucceededPayload? SuccessPayload = null,
    InventoryAllocationCommandRejectedPayload? RejectionPayload = null,
    ReturnedUnitsAppliedPayload? ReturnedUnitsAppliedPayload = null,
    ReturnedUnitsRejectedPayload? ReturnedUnitsRejectedPayload = null)
{
    public static AllocationCommandOutcome Succeeded(
        string commandType,
        string operationKey,
        Guid rentalOrderId,
        Guid rentalItemId,
        InventoryAllocationCommandSucceededPayload successPayload) =>
        new(true, commandType, operationKey, rentalOrderId, rentalItemId, SuccessPayload: successPayload);

    public static AllocationCommandOutcome Rejected(
        string commandType,
        string operationKey,
        Guid rentalOrderId,
        Guid rentalItemId,
        string errorCode,
        string errorMessage,
        DateTimeOffset occurredAt,
        string? allocationGroupId = null) =>
        new(false, commandType, operationKey, rentalOrderId, rentalItemId,
            RejectionPayload: new InventoryAllocationCommandRejectedPayload(
                rentalOrderId,
                rentalItemId,
                operationKey,
                commandType,
                errorCode,
                errorMessage,
                occurredAt,
                allocationGroupId));

    public static AllocationCommandOutcome ReturnApplied(
        string operationKey,
        Guid rentalOrderId,
        Guid rentalItemId,
        ReturnedUnitsAppliedPayload payload) =>
        new(true, AllocationMessageTypes.ApplyReturnedUnits, operationKey, rentalOrderId, rentalItemId,
            ReturnedUnitsAppliedPayload: payload);

    public static AllocationCommandOutcome ReturnRejected(
        string operationKey,
        Guid rentalOrderId,
        Guid rentalItemId,
        string allocationGroupId,
        Guid returnRecordId,
        string errorCode,
        string errorMessage) =>
        new(false, AllocationMessageTypes.ApplyReturnedUnits, operationKey, rentalOrderId, rentalItemId,
            ReturnedUnitsRejectedPayload: new ReturnedUnitsRejectedPayload(
                rentalOrderId,
                rentalItemId,
                allocationGroupId,
                returnRecordId,
                operationKey,
                errorCode,
                errorMessage));
}
