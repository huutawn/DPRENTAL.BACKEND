using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Application.Kafka.Saga;

/// <summary>
/// Đại diện cho tên bước nghiệp vụ hiện tại trong tiến trình Saga.
/// </summary>
public sealed record SagaStep(string Name)
{
    public static implicit operator string(SagaStep step) => step.Name;
    public static implicit operator SagaStep(string name) => new(name);
}

/// <summary>
/// Metadata của một timeout đã lên lịch trong Saga.
/// TimeoutId ổn định và không đổi qua các lần retry.
/// </summary>
public sealed record SagaTimeout(Guid TimeoutId, string StepName, DateTimeOffset Deadline);

/// <summary>
/// Kết quả chuyển đổi trạng thái (Transition) của một bước thực thi Saga.
/// Bao gồm trạng thái mới, bước mới, dữ liệu bối cảnh, các message cần phát ra Outbox và timeout tiếp theo nếu có.
/// </summary>
public sealed record SagaTransition
{
    public required SagaState State { get; init; }
    public required SagaStep Step { get; init; }
    public required string DataJson { get; init; }
    public IReadOnlyList<OutboxMessage> OutgoingMessages { get; init; } = [];
    public SagaTimeout? NextTimeout { get; init; }
}
