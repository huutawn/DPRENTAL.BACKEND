namespace Dprental.AnalyticsService.Domain.IRepositories;

/// <summary>
/// Lưu mọi thay đổi đang chờ trong MỘT lần. EF Core bọc mỗi lần SaveChanges trong một transaction,
/// nên facts và inbox được lưu nguyên khối: hoặc cả hai, hoặc không gì cả.
/// </summary>
public interface IAnalyticsUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Bỏ mọi thay đổi chưa lưu (dữ liệu lỗi, hoặc chuẩn bị thử lại).</summary>
    void DiscardChanges();
}
