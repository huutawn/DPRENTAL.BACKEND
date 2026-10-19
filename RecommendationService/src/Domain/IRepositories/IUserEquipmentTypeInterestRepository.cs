namespace Dprental.RecommendationService.Domain.IRepositories;

public sealed record UserEquipmentTypeInterestItem(Guid EquipmentTypeId, long Score);

public interface IUserEquipmentTypeInterestRepository
{
    Task UpsertAsync(Guid userId, Guid equipmentTypeId, short scoreWeight, DateTimeOffset occurredAt, CancellationToken cancellationToken);

    Task<IReadOnlyList<UserEquipmentTypeInterestItem>> GetTopInterestsAsync(Guid userId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> GetDistinctUserIdsPagedAsync(int skip, int take, CancellationToken cancellationToken);
}
