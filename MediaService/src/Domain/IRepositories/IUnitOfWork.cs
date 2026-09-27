namespace Dprental.MediaService.Domain.IRepositories;

/// <summary>
/// Quản lý giao dịch và lưu thay đổi vào cơ sở dữ liệu.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
