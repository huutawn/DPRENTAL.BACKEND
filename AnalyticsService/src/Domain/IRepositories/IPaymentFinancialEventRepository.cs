using Dprental.AnalyticsService.Domain.Entities;

namespace Dprental.AnalyticsService.Domain.IRepositories;

/// <summary>
/// Đọc/ghi payment_financial_events. Một dòng là một sự kiện tài chính bất biến; chống trùng bằng
/// MessageId (giao lại) và LogicalEventId (Payment phát lại cùng nghiệp vụ với messageId mới).
/// </summary>
public interface IPaymentFinancialEventRepository
{
    Task<bool> ExistsMessageAsync(Guid messageId, CancellationToken cancellationToken = default);

    Task<PaymentFinancialEvent?> FindByLogicalEventIdAsync(string logicalEventId, CancellationToken cancellationToken = default);

    void Add(PaymentFinancialEvent financialEvent);
}
