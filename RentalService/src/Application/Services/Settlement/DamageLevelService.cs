using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Application.IServices.Settlement;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Services.Settlement;

public sealed class DamageLevelService(IDamageLevelRepository damageLevels) : IDamageLevelService
{
    public async Task<IReadOnlyList<DamageLevelDto>> GetAllAsync(CancellationToken cancellationToken = default) =>
        (await damageLevels.GetAllAsync(cancellationToken))
            .Select(l => new DamageLevelDto(l.Level, l.Name, l.Description, l.Percent))
            .ToList();
}
