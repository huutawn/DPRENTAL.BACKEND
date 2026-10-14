namespace Dprental.RentalService.Domain.IRepositories;

/// <summary>
/// Sinh mã đơn hàng dạng DH-YYYY-NNNNNN, không trùng giữa các request đồng thời.
/// </summary>
public interface IOrderCodeGenerator
{
    Task<string> NextAsync(DateTimeOffset utcNow, CancellationToken cancellationToken = default);
}
