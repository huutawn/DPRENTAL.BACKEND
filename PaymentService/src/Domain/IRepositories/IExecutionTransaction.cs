namespace Dprental.PaymentService.Domain.IRepositories;

/// <summary>
/// Giao dịch thực thi nguyên tử ở tầng repository, không lộ kiểu EF.
/// </summary>
public interface IExecutionTransaction : IAsyncDisposable
{
    public static readonly IExecutionTransaction None = new NullExecutionTransaction();

    Task CommitAsync(CancellationToken cancellationToken = default);
    Task RollbackAsync(CancellationToken cancellationToken = default);

    private sealed class NullExecutionTransaction : IExecutionTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
