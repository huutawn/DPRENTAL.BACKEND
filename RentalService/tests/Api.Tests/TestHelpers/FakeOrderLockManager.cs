using System.Collections.Concurrent;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Api.Tests.TestHelpers;

public sealed class FakeOrderLockManager : IOrderLockManager
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Semaphores = new();

    public async Task<IAsyncDisposable> AcquireOrderLockAsync(
        Guid orderId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var sem = Semaphores.GetOrAdd(orderId, _ => new SemaphoreSlim(1, 1));
        bool entered = await sem.WaitAsync(timeout ?? TimeSpan.FromSeconds(5), cancellationToken);
        if (!entered)
        {
            throw new TimeoutException($"Fake lock timeout for order {orderId}");
        }
        return new Releaser(sem);
    }

    public Task<IAsyncDisposable> AcquireRenterLockAsync(
        Guid renterUserId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        AcquireOrderLockAsync(renterUserId, timeout, cancellationToken);

    private sealed class Releaser : IAsyncDisposable
    {
        private readonly SemaphoreSlim _sem;
        private int _disposed;

        public Releaser(SemaphoreSlim sem) => _sem = sem;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _sem.Release();
            }
            return ValueTask.CompletedTask;
        }
    }
}
