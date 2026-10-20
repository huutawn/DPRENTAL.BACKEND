using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.DTOs.Allocations;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Application.IServices;

/// <summary>
/// Giao diện dịch vụ ứng dụng quản lý vòng đời giữ chỗ và phân bổ tài sản (Hold &amp; Allocation Core).
/// </summary>
public interface IInventoryAllocationService
{
    /// <summary>
    /// Tạo mới một lượt giữ chỗ tạm thời (Hold) hoặc trả về lượt hiện tại nếu idempotent replay.
    /// </summary>
    Task<Result<CreateHoldResult>> CreateHoldAsync(
        Guid assetId,
        CreateHoldRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);
    /// <summary>
    /// Tạo mới một lượt giữ chỗ tạm thời theo nhóm (Group Hold) trên EquipmentListing hoặc trả về lượt hiện tại nếu idempotent replay.
    /// </summary>
    Task<Result<ListingHoldResponse>> CreateListingHoldAsync(
        Guid listingId,
        CreateListingHoldRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);


    /// <summary>
    /// Chuyển đổi lượt giữ chỗ còn hạn sang trạng thái phân bổ chính thức (Held -&gt; Allocated).
    /// </summary>
    Task<Result<AllocationResponse>> AllocateAsync(
        Guid allocationId,
        AllocateRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Xử lý lệnh CreateHold bất đồng bộ từ Kafka hoặc orchestration.
    /// </summary>
    Task<Result<AllocationCommandOutcome>> ProcessCreateHoldCommandAsync(
        MessageEnvelope<CreateHoldPayload> envelope,
        IInventoryCallerContext caller,
        string handlerName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<AllocationCommandOutcome>(Error.Failure("NOT_IMPLEMENTED", "Not implemented")));

    /// <summary>
    /// Xử lý lệnh ExtendHold bất đồng bộ từ Kafka hoặc orchestration.
    /// </summary>
    Task<Result<AllocationCommandOutcome>> ProcessExtendHoldCommandAsync(
        MessageEnvelope<ExtendHoldPayload> envelope,
        IInventoryCallerContext caller,
        string handlerName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<AllocationCommandOutcome>(Error.Failure("NOT_IMPLEMENTED", "Not implemented")));

    /// <summary>
    /// Xử lý lệnh ConfirmAllocation bất đồng bộ từ Kafka hoặc orchestration.
    /// </summary>
    Task<Result<AllocationCommandOutcome>> ProcessConfirmAllocationCommandAsync(
        MessageEnvelope<ConfirmAllocationPayload> envelope,
        IInventoryCallerContext caller,
        string handlerName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<AllocationCommandOutcome>(Error.Failure("NOT_IMPLEMENTED", "Not implemented")));

    /// <summary>
    /// Xử lý lệnh ReleaseAllocation bất đồng bộ từ Kafka hoặc orchestration.
    /// </summary>
    Task<Result<AllocationCommandOutcome>> ProcessReleaseAllocationCommandAsync(
        MessageEnvelope<ReleaseAllocationPayload> envelope,
        IInventoryCallerContext caller,
        string handlerName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<AllocationCommandOutcome>(Error.Failure("NOT_IMPLEMENTED", "Not implemented")));

    /// <summary>
    /// Xử lý lệnh MarkAssetsHandedOver bất đồng bộ từ Kafka hoặc orchestration.
    /// </summary>
    Task<Result<AllocationCommandOutcome>> ProcessMarkAssetsHandedOverCommandAsync(
        MessageEnvelope<MarkAssetsHandedOverPayload> envelope,
        IInventoryCallerContext caller,
        string handlerName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<AllocationCommandOutcome>(Error.Failure("NOT_IMPLEMENTED", "Not implemented")));

    /// <summary>
    /// Xử lý lệnh ApplyReturnedUnits bất đồng bộ từ Kafka hoặc orchestration.
    /// </summary>
    Task<Result<AllocationCommandOutcome>> ProcessApplyReturnedUnitsCommandAsync(
        MessageEnvelope<ApplyReturnedUnitsCommandPayload> envelope,
        IInventoryCallerContext caller,
        string handlerName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<AllocationCommandOutcome>(Error.Failure("NOT_IMPLEMENTED", "Not implemented")));
}
