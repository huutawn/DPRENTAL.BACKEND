namespace Dprental.NotificationService.Domain.Entities;

/// <summary>
/// Projection trạng thái tổng hợp của một aggregate (Order, Item, ReturnRequest) không phụ thuộc vào từng recipient cụ thể.
/// </summary>
public sealed record AggregateStateProjection(
    string Scope,
    Guid AggregateId,
    long MaxVersion,
    bool IsPaid,
    bool IsTerminal,
    string? TerminalState,
    long? TerminalVersion
);
