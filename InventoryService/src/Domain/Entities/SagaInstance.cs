using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể lưu trữ trạng thái của một phiên saga phân tán điều phối giao dịch đa dịch vụ (Saga Pattern).
/// Quản lý dữ liệu bối cảnh, trạng thái hiện tại và lịch trình xử lý các bước tiếp theo hoặc bù trừ.
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
    /// Tên loại saga nghiệp vụ (ví dụ: CreateReservationSaga, CancelReservationSaga).
    /// </summary>
    [Column("saga_type")]
    [MaxLength(100)]
    public string SagaType { get; private set; } = string.Empty;

    /// <summary>
    /// Mã tương quan chuỗi xử lý (Correlation ID) kết hợp với SagaType tạo thành khóa duy nhất.
    /// </summary>
    [Column("correlation_id")]
    [MaxLength(100)]
    public string CorrelationId { get; private set; } = string.Empty;

    /// <summary>
    /// Trạng thái hiện tại của phiên saga (Started, Executing, Completed, Compensating, Failed).
    /// </summary>
    [Column("state")]
    [MaxLength(50)]
    public string State { get; private set; } = string.Empty;

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
    /// Thời điểm dự kiến tiếp tục xử lý bước tiếp theo hoặc timeout của saga (UTC).
    /// </summary>
    [Column("next_process_at")]
    public DateTimeOffset? NextProcessAt { get; private set; }

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
    /// <param name="id">ID phiên saga.</param>
    /// <param name="sagaType">Loại saga.</param>
    /// <param name="correlationId">Correlation ID duy nhất.</param>
    /// <param name="initialState">Trạng thái khởi đầu.</param>
    /// <param name="data">Dữ liệu bối cảnh JSONB.</param>
    /// <param name="now">Thời điểm tạo (UTC).</param>
    /// <param name="nextProcessAt">Thời điểm xử lý tiếp theo nếu có.</param>
    public static SagaInstance Create(
        Guid id,
        string sagaType,
        string correlationId,
        string initialState,
        string data,
        DateTimeOffset now,
        DateTimeOffset? nextProcessAt = null)
    {
        return new SagaInstance
        {
            Id = id,
            SagaType = sagaType,
            CorrelationId = correlationId,
            State = initialState,
            Data = data,
            CreatedAt = now,
            UpdatedAt = now,
            NextProcessAt = nextProcessAt
        };
    }

    /// <summary>
    /// Cập nhật trạng thái và dữ liệu bối cảnh mới của saga.
    /// </summary>
    /// <param name="newState">Trạng thái mới.</param>
    /// <param name="newData">Dữ liệu mới JSONB.</param>
    /// <param name="now">Thời điểm cập nhật (UTC).</param>
    /// <param name="nextProcessAt">Thời điểm xử lý tiếp theo nếu có.</param>
    public void TransitionTo(string newState, string newData, DateTimeOffset now, DateTimeOffset? nextProcessAt = null)
    {
        State = newState;
        Data = newData;
        UpdatedAt = now;
        NextProcessAt = nextProcessAt;
    }
}
