using Dprental.AnalyticsService.Domain.Entities;

namespace Dprental.AnalyticsService.Domain.IRepositories;

/// <summary>
/// Truy cập financial_settlement_facts và refund_facts cho projector tài chính.
/// Dữ liệu tài chính chỉ thêm mới, không sửa.
/// </summary>
public interface IFinancialFactRepository
{
    Task<FinancialSettlementFact?> FindSettlementAsync(string settlementRef, CancellationToken cancellationToken = default);

    Task<RefundFact?> FindRefundAsync(string refundRef, Guid rentalItemId, CancellationToken cancellationToken = default);

    void AddSettlement(FinancialSettlementFact settlement);

    void AddRefund(RefundFact refund);
}
