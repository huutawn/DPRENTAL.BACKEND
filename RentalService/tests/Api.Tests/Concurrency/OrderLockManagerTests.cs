using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Concurrency;

public sealed class OrderLockManagerTests
{
    [Fact]
    public void ComputeLockKey_ProducesDeterministicKey()
    {
        Guid id = Guid.Parse("7b8979c3-7182-4147-920f-cb1c98126b85");
        long key1 = PostgresOrderLockManager.ComputeLockKey(id);
        long key2 = PostgresOrderLockManager.ComputeLockKey(id);

        Assert.Equal(key1, key2);
        Assert.NotEqual(0, key1);
    }

    private static string TestConnectionString =>
        Dprental.RentalService.Api.Tests.TestHelpers.PostgresTestDatabaseHelper.BaseConnectionString;

    private static IConfiguration CreateConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("ConnectionStrings:Postgres", TestConnectionString) })
            .Build();

    [Fact]
    public async Task AcquireOrderLock_DifferentOrderIds_DoNotBlockEachOther()
    {
        var manager = new PostgresOrderLockManager(CreateConfig(), NullLogger<PostgresOrderLockManager>.Instance);
        Guid order1 = Guid.NewGuid();
        Guid order2 = Guid.NewGuid();

        await using var lock1 = await manager.AcquireOrderLockAsync(order1, TimeSpan.FromSeconds(15));
        await using var lock2 = await manager.AcquireOrderLockAsync(order2, TimeSpan.FromSeconds(15));

        Assert.NotNull(lock1);
        Assert.NotNull(lock2);
    }

    [Fact]
    public async Task AcquireOrderLock_SameOrderId_SerializesExecution()
    {
        var manager = new PostgresOrderLockManager(CreateConfig(), NullLogger<PostgresOrderLockManager>.Instance);
        Guid orderId = Guid.NewGuid();
        IAsyncDisposable? lock1 = null;
        bool lock2Acquired = false;
        var lock2Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? lock2Task = null;

        try
        {
            lock1 = await manager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(15));

            lock2Task = Task.Run(async () =>
            {
                lock2Started.SetResult(true);
                await using var lock2 = await manager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(15));
                lock2Acquired = true;
            });

            await lock2Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Allow lock2 to reach its acquisition loop while lock1 is definitely held
            await Task.Delay(200);
            Assert.False(lock2Acquired);
            Assert.False(lock2Task.IsCompleted);
        }
        finally
        {
            if (lock1 != null)
            {
                await lock1.DisposeAsync();
            }
        }

        Assert.NotNull(lock2Task);
        await lock2Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(lock2Acquired);
    }

    [Fact]
    public async Task AcquireOrderLock_WhenTimeoutExpires_ThrowsTimeoutException()
    {
        var manager = new PostgresOrderLockManager(CreateConfig(), NullLogger<PostgresOrderLockManager>.Instance);
        Guid orderId = Guid.NewGuid();
        IAsyncDisposable? lock1 = null;
        try
        {
            lock1 = await manager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(15));

            await Assert.ThrowsAsync<TimeoutException>(async () =>
            {
                await manager.AcquireOrderLockAsync(orderId, TimeSpan.FromMilliseconds(200));
            });
        }
        finally
        {
            if (lock1 != null)
            {
                await lock1.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task AcquireOrderLock_WithoutConnectionString_ThrowsInvalidOperationException()
    {
        var manager = new PostgresOrderLockManager(new ConfigurationBuilder().Build(), NullLogger<PostgresOrderLockManager>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.AcquireOrderLockAsync(Guid.NewGuid()));
    }
}
