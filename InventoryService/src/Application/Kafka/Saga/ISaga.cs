using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Application.Kafka.Saga;

/// <summary>
/// Hợp đồng của một Saga phân tán tiếp nhận thông điệp hoặc timeout callback.
/// Trả về SagaTransition để Orchestrator cập nhật trạng thái và xuất bản Outbox atomically.
/// </summary>
public interface ISaga
{
    /// <summary>
    /// Tên loại saga nghiệp vụ định danh duy nhất (ví dụ: EquipmentHoldSaga, ReservationFulfillmentSaga).
    /// </summary>
    string SagaType { get; }

    /// <summary>
    /// Xử lý một sự kiện/lệnh đầu vào và xác định bước chuyển tiếp theo.
    /// </summary>
    Task<SagaTransition> HandleMessageAsync(
        SagaInstance instance,
        string messageType,
        string payloadJson,
        CancellationToken cancellationToken);

    /// <summary>
    /// Xử lý sự kiện timeout đã đến hạn.
    /// </summary>
    Task<SagaTransition> HandleTimeoutAsync(
        SagaInstance instance,
        SagaTimeout timeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// Bộ điều phối thực thi phiên Saga phân tán.
/// Khóa và đọc lại saga bằng FOR UPDATE; commit state transition, timeout và outgoing Outbox cùng transaction với Inbox.
/// </summary>
public interface ISagaOrchestrator
{
    /// <summary>
    /// Điều phối xử lý thông điệp cho một phiên saga:
    /// Khóa phiên saga, chạy HandleMessageAsync, cập nhật state/step/timeout và stage Outbox.
    /// </summary>
    Task<Result<SagaInstance>> ProcessMessageAsync(
        string sagaType,
        Guid organizationId,
        string correlationId,
        string messageType,
        string payloadJson,
        Func<SagaInstance>? initialFactory = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Điều phối xử lý timeout callback cho một phiên saga:
    /// Khóa phiên saga, chạy HandleTimeoutAsync, cập nhật state/step/timeout và stage Outbox atomically.
    /// </summary>
    Task<Result<SagaInstance>> ProcessTimeoutAsync(
        Guid sagaId,
        Guid expectedTimeoutId,
        Guid leaseToken,
        CancellationToken cancellationToken = default);
}
