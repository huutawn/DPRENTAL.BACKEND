namespace Dprental.RecommendationService.Domain.IRepositories;

public interface IInteractionUnitOfWork
{
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken);

    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken);

    Task RebuildProjectionsAsync(CancellationToken cancellationToken);
}
