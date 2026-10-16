namespace Dprental.PaymentService.Domain.Entities;

/// <summary>Trạng thái tài chính của settlement (khác với chuỗi trạng thái trong DTO API).</summary>
public enum SettlementState
{
    PendingManualRefund = 1,
    Completed = 2
}