namespace Dprental.AnalyticsService.Domain.IRepositories;

public sealed record SupplierCandidatePage(int Total, IReadOnlyList<Guid> SupplierIds);

public interface ISupplierDashboardQueryRepository
{
    Task<SupplierCandidatePage> GetCandidatesAsync(
        DateTimeOffset startUtc, DateTimeOffset endUtc, int offset, int pageSize,
        CancellationToken cancellationToken = default);
}
