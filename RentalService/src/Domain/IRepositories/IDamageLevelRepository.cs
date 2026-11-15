using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Domain.IRepositories;

/// <summary>Bảng mức hư hại hiện hành; chỉ đọc, không theo dõi thay đổi.</summary>
public interface IDamageLevelRepository
{
    Task<IReadOnlyList<DamageLevel>> GetAllAsync(CancellationToken cancellationToken = default);
}
