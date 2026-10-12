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
        Environment.GetEnvironmentVariable("POSTGRESQL_TEST_CONNECTION_STRING")
        ?? Environment.GetEnvironmentVariable("RENTAL_TEST_POSTGRES_CONNECTION")
        ?? "Host=127.0.0.1;Port=5433;Database=postgres;Username=inventoryservice;Password=inventory_test_secret_123!;Include Error Detail=true";

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

        await using var lock1 = await manager.AcquireOrderLockAsync(order1, TimeSpan.FromSeconds(2));
        await using var lock2 = await manager.AcquireOrderLockAsync(order2, TimeSpan.FromSeconds(2));

        Assert.NotNull(lock1);
        Assert.NotNull(lock2);
    }
    [Fact]
    public async Task AcquireOrderLock_SameOrderId_SerializesExecution()
    {
        var manager = new PostgresOrderLockManager(CreateConfig(), NullLogger<PostgresOrderLockManager>.Instance);
        Guid orderId = Guid.NewGuid();
        var lock1 = await manager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(2));

        bool lock2Acquired = false;
        var task = Task.Run(async () =>
        {
            await using var lock2 = await manager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(2));
            lock2Acquired = true;
        });

        // lock2 should be waiting while lock1 is held
        await Task.Delay(100);
        Assert.False(lock2Acquired);

        // Release lock1
        await lock1.DisposeAsync();

        // lock2 can now proceed
        await task;
        Assert.True(lock2Acquired);
    }

    [Fact]
    public async Task AcquireOrderLock_WhenTimeoutExpires_ThrowsTimeoutException()
    {
        var manager = new PostgresOrderLockManager(CreateConfig(), NullLogger<PostgresOrderLockManager>.Instance);
        Guid orderId = Guid.NewGuid();
        await using var lock1 = await manager.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await manager.AcquireOrderLockAsync(orderId, TimeSpan.FromMilliseconds(200));
        });
    }

    [Fact]
    public async Task AcquireOrderLock_WithoutConnectionString_ThrowsInvalidOperationException()
    {
        var manager = new PostgresOrderLockManager(new ConfigurationBuilder().Build(), NullLogger<PostgresOrderLockManager>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.AcquireOrderLockAsync(Guid.NewGuid()));
    }
}
