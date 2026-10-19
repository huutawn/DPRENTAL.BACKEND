namespace Dprental.RecommendationService.Application.IServices;

public interface ISnapshotPublishingService
{
    Task PublishSnapshotsAsync(CancellationToken cancellationToken);
}
