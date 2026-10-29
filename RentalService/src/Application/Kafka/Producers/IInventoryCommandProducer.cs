using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Kafka.Producers;

/// <summary>
/// Ghi lệnh Inventory vào Outbox bền vững, mặc định gửi tới inventory.allocation.commands.v1.
/// Caller mở và commit transaction cùng thay đổi nghiệp vụ; publisher gửi broker sau đó.
/// Enqueue thành công chưa chứng minh Inventory đã nhận hoặc thực thi lệnh; phải chờ result event.
/// </summary>
public interface IInventoryCommandProducer
{
    /// <summary>Yêu cầu đóng allocation của từng unit trả lại và bảo vệ máy hỏng; chờ ReturnedUnitsApplied/Rejected.</summary>
    Task<Result<ProducerEnqueueResult>> EnqueueApplyReturnedUnitsAsync(
        ApplyReturnedUnitsCommandPayload command,
        CancellationToken cancellationToken = default);
    /// <summary>Yêu cầu Inventory giữ số máy rảnh liên tục theo listing/kỳ thuê; dùng ở các nhánh enqueue Kafka.</summary>
    Task<Result<ProducerEnqueueResult>> EnqueueCreateHoldAsync(
        CreateHoldCommandPayload command,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Yêu cầu gia hạn hold tới mốc đã được nghiệp vụ xác định, không tự tính deadline tại producer.</summary>
    Task<Result<ProducerEnqueueResult>> EnqueueExtendHoldAsync(
        ExtendHoldCommandPayload command,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Ghi lệnh xác nhận allocation cho nhánh dùng Kafka; supplier accept hiện có đường HTTP riêng.</summary>
    Task<Result<ProducerEnqueueResult>> EnqueueConfirmAllocationAsync(
        ConfirmAllocationCommandPayload command,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Yêu cầu giải phóng allocation khi hủy/hết hạn hoặc bù trừ; lưu lý do và khóa tác vụ.</summary>
    Task<Result<ProducerEnqueueResult>> EnqueueReleaseAllocationAsync(
        ReleaseAllocationCommandPayload command,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Yêu cầu ghi nhận bàn giao ở Inventory; Rental chỉ chốt InRental sau kết quả thành công.</summary>
    Task<Result<ProducerEnqueueResult>> EnqueueMarkAssetsHandedOverAsync(
        MarkAssetsHandedOverCommandPayload command,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Yêu cầu bảo vệ lịch của máy quá hạn chưa trả, không yêu cầu giải phóng máy.</summary>
    Task<Result<ProducerEnqueueResult>> EnqueueMarkAssetsOverdueAsync(
        MarkAssetsOverdueCommandPayload command,
        string? correlationId = null,
        Guid? causationId = null,
        CancellationToken cancellationToken = default);

}
