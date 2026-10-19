namespace Dprental.RecommendationService.Domain.IRepositories;

public interface IValkeyProjectionRepository
{
    Task<bool> SetUserEquipmentTypesSnapshotAsync(Guid userId, string jsonSnapshot, string generatedAtIso, TimeSpan ttl, CancellationToken cancellationToken);

    Task<bool> SetGlobalPopularListingsSnapshotAsync(string jsonSnapshot, string generatedAtIso, TimeSpan ttl, CancellationToken cancellationToken);

    Task<string?> GetUserEquipmentTypesSnapshotAsync(Guid userId, CancellationToken cancellationToken);

    Task<string?> GetGlobalPopularListingsSnapshotAsync(CancellationToken cancellationToken);
}
