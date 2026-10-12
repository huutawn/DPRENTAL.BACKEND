namespace Dprental.RentalService.Domain.IRepositories;

/// <summary>
/// Contract Unit of Work để quản lý giao dịch và lưu thay đổi aggregate.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Bỏ mọi thay đổi chưa lưu đang được theo dõi. Gọi sau khi lưu thất bại để lần lưu sau
    /// trong cùng request không ghi lại dữ liệu hỏng.
    /// </summary>
    void DiscardChanges();
}
