using Dprental.RentalService.Application.DTOs.Settlement;

namespace Dprental.RentalService.Application.IServices.Settlement;

public interface IDamageLevelService
{
    /// <summary>Bảng mức hư hại hiện hành, sắp theo mức tăng dần. Đơn đã checkout dùng snapshot riêng.</summary>
    Task<IReadOnlyList<DamageLevelDto>> GetAllAsync(CancellationToken cancellationToken = default);
}
