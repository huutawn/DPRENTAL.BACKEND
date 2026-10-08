namespace Dprental.RentalService.Domain.IRepositories;

/// <summary>
/// Contract Unit of Work để quản lý giao dịch và lưu thay đổi aggregate.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
