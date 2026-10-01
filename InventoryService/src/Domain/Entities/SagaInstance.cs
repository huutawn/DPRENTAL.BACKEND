using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.InventoryService.Common.Enums;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể lưu trữ trạng thái của một phiên saga phân tán điều phối giao dịch đa dịch vụ (Saga Pattern).
/// Quản lý dữ liệu bối cảnh, trạng thái hiện tại, bước thực thi và lịch trình timeout.
/// Identity duy nhất là (SagaType, OrganizationId, CorrelationId).
/// </summary>
[Table("saga_instances", Schema = "inventory")]
public sealed class SagaInstance
{
    /// <summary>
    /// Định danh duy nhất toàn cục của phiên saga.
    /// </summary>
    [Key]
    [Column("id")]
    public Guid Id { get; private set; }

    /// <summary>
    /// Tên loại saga nghiệp vụ (ví dụ: EquipmentHoldSaga, ReservationFulfillmentSaga).
    /// </summary>
    [Column("saga_type")]
    [MaxLength(100)]
    public string SagaType { get; private set; } = string.Empty;

    /// <summary>
    /// Định danh tổ chức sở hữu phiên saga (Tenant Isolation).
    /// </summary>
    [Column("organization_id")]
    public Guid OrganizationId { get; private set; }

    /// <summary>
    /// Mã tương quan chuỗi xử lý (Correlation ID) kết hợp với SagaType và OrganizationId tạo thành khóa duy nhất.
    /// </summary>
    [Column("correlation_id")]
    [MaxLength(100)]
    public string CorrelationId { get; private set; } = string.Empty;

    /// <summary>
    /// Trạng thái hiện tại của phiên saga theo enum SagaState.
    /// </summary>
    [Column("state")]
    public SagaState State { get; private set; }

    /// <summary>
    /// Tên bước nghiệp vụ hiện tại của saga (ví dụ: Started, AwaitingEquipmentHold, Completed).
    /// </summary>
    [Column("current_step")]
    [MaxLength(100)]
    public string CurrentStep { get; private set; } = string.Empty;

    /// <summary>
    /// Dữ liệu trạng thái và bối cảnh của saga dưới định dạng JSONB của PostgreSQL.
    /// </summary>
    [Column("data", TypeName = "jsonb")]
    public string Data { get; private set; } = string.Empty;

    /// <summary>
    /// Thời điểm khởi tạo phiên saga (UTC).
    /// </summary>
    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Thời điểm cập nhật trạng thái saga gần nhất (UTC).
    /// </summary>
    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Định danh duy nhất và ổn định của timeout đang chờ kích hoạt. Null khi không có timeout hoặc saga ở trạng thái terminal.
    /// </summary>
    [Column("timeout_id")]
    public Guid? TimeoutId { get; private set; }

    /// <summary>
    /// Hạn chót kích hoạt timeout (UTC).
    /// </summary>
    [Column("timeout_deadline")]
    public DateTimeOffset? TimeoutDeadline { get; private set; }

    /// <summary>
    /// Token lease của worker đang giữ quyền xử lý timeout (chống worker cạnh tranh xử lý 2 lần).
    /// </summary>
    [Column("timeout_lease_token")]
    public Guid? TimeoutLeaseToken { get; private set; }

    /// <summary>
    /// Thời hạn hiệu lực của timeout lease lock (UTC).
    /// </summary>
    [Column("timeout_lease_expires_at")]
    public DateTimeOffset? TimeoutLeaseExpiresAt { get; private set; }

    /// <summary>
    /// Số lần đã thử xử lý timeout thất bại (tối đa 5 lần theo budget).
    /// </summary>
    [Column("timeout_retry_count")]
    public int TimeoutRetryCount { get; private set; }

    /// <summary>
    /// Chi tiết lỗi gần nhất khi thực thi timeout callback phục vụ chẩn đoán sự cố.
    /// </summary>
    [Column("timeout_last_error")]
    [MaxLength(2000)]
    public string? TimeoutLastError { get; private set; }

    /// <summary>
    /// Concurrency token ánh xạ tới xmin của PostgreSQL phục vụ phát hiện stale updates khi saga cập nhật trạng thái.
    /// </summary>
    [Timestamp]
    [Column("xmin")]
    public uint Version { get; set; }

    /// <summary>
    /// Khởi tạo private phục vụ EF Core.
    /// </summary>
    private SagaInstance()
    {
    }

    /// <summary>
    /// Tạo mới một phiên phối hợp saga.
    /// </summary>
    public static SagaInstance Create(
        Guid id,
        string sagaType,
        Guid organizationId,
        string correlationId,
        SagaState initialState,
        string initialStep,
        string data,
        DateTimeOffset now,
        Guid? timeoutId = null,
        DateTimeOffset? timeoutDeadline = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sagaType);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(initialStep);

        return new SagaInstance
        {
            Id = id,
            SagaType = sagaType,
            OrganizationId = organizationId,
            CorrelationId = correlationId,
            State = initialState,
            CurrentStep = initialStep,
            Data = data,
            CreatedAt = now,
            UpdatedAt = now,
            TimeoutId = timeoutId,
            TimeoutDeadline = timeoutDeadline,
            TimeoutRetryCount = 0
        };
    }

    /// <summary>
    /// Cập nhật trạng thái, bước chuyển và dữ liệu bối cảnh mới của saga.
    /// Khi chuyển sang terminal state (Completed hoặc Failed), timeout metadata tự động được xóa.
    /// </summary>
    public void TransitionTo(
        SagaState newState,
        string newStep,
        string newData,
        DateTimeOffset now,
        Guid? timeoutId = null,
        DateTimeOffset? timeoutDeadline = null)
    {
        State = newState;
        CurrentStep = newStep;
        Data = newData;
        UpdatedAt = now;

        if (newState is SagaState.Completed or SagaState.Failed)
        {
            TimeoutId = null;
            TimeoutDeadline = null;
            TimeoutRetryCount = 0;
            TimeoutLastError = null;
            TimeoutLeaseToken = null;
            TimeoutLeaseExpiresAt = null;
        }
        else
        {
            TimeoutId = timeoutId;
            TimeoutDeadline = timeoutDeadline;
            TimeoutRetryCount = 0;
            TimeoutLastError = null;
            TimeoutLeaseToken = null;
            TimeoutLeaseExpiresAt = null;
        }
    }

    /// <summary>
    /// Ghi nhận lỗi khi xử lý timeout và tăng số lần retry.
    /// </summary>
    public void RecordTimeoutFailure(string error, DateTimeOffset nextDeadline)
    {
        TimeoutRetryCount++;
        TimeoutLastError = error;
        TimeoutDeadline = nextDeadline;
        TimeoutLeaseToken = null;
        TimeoutLeaseExpiresAt = null;
    }

    /// <summary>
    /// Gán lease timeout cho worker đang claim.
    /// </summary>
    public void AssignTimeoutLease(Guid leaseToken, DateTimeOffset expiresAt)
    {
        TimeoutLeaseToken = leaseToken;
        TimeoutLeaseExpiresAt = expiresAt;
    }

    /// <summary>
    /// Xóa timeout đang chờ.
    /// </summary>
    public void ClearTimeout()
    {
        TimeoutId = null;
        TimeoutDeadline = null;
        TimeoutRetryCount = 0;
        TimeoutLeaseToken = null;
        TimeoutLeaseExpiresAt = null;
    }
}
