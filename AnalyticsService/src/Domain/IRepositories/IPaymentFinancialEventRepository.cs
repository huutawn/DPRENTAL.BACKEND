using Dprental.AnalyticsService.Common.Enums;
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

    Task<FinancialFactMetrics> GetFinancialMetricsAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        Guid? supplierId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<DateOnly, decimal[]>> GetRevenueBucketsAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        RevenueGranularity granularity,
        Guid? supplierId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, SupplierFinancialStatRow>> GetSupplierFinancialStatsAsync(
        IReadOnlyCollection<Guid> supplierIds,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default);
}
