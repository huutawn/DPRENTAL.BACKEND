using Dprental.RecommendationService.Domain.IRepositories;
using Dprental.RecommendationService.Domain.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.RecommendationService.Api.Tests.Integration;

public sealed class ValkeyConnectionLifecycleTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValkeyConnectionProvider_RejectsMissingConnectionString(string? connectionString)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ValkeyConnectionProvider(connectionString!, NullLogger<ValkeyConnectionProvider>.Instance));

        Assert.Contains("ConnectionStrings:Redis is missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValkeyConnectionProvider_RejectsInvalidConnectionString()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ValkeyConnectionProvider("127.0.0.1:999999", NullLogger<ValkeyConnectionProvider>.Instance));

        Assert.Contains("Invalid ConnectionStrings:Redis", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValkeyConnectionProvider_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var provider = new ValkeyConnectionProvider("127.0.0.1:6379", NullLogger<ValkeyConnectionProvider>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await provider.GetMultiplexerAsync(cts.Token));
    }

    [Fact]
    public async Task ValkeyProjectionRepository_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var provider = new ValkeyConnectionProvider("127.0.0.1:6379", NullLogger<ValkeyConnectionProvider>.Instance);
        var repo = new ValkeyProjectionRepository(provider, NullLogger<ValkeyProjectionRepository>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await repo.SetGlobalPopularListingsSnapshotAsync("{}", "2026-10-19T10:00:00Z", TimeSpan.FromSeconds(300), cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await repo.GetGlobalPopularListingsSnapshotAsync(cts.Token));
    }

    [Fact]
    public async Task NullValkeyConnectionProvider_ReturnsNullSafely()
    {
        using var provider = new NullValkeyConnectionProvider();
        Assert.False(provider.IsConnected);

        var mux = await provider.GetMultiplexerAsync(CancellationToken.None);
        Assert.Null(mux);

        var repo = new ValkeyProjectionRepository(provider, NullLogger<ValkeyProjectionRepository>.Instance);
        bool writeSuccess = await repo.SetGlobalPopularListingsSnapshotAsync("{}", "2026-10-19T10:00:00Z", TimeSpan.FromSeconds(300), CancellationToken.None);
        Assert.False(writeSuccess);

        string? read = await repo.GetGlobalPopularListingsSnapshotAsync(CancellationToken.None);
        Assert.Null(read);
    }
}
