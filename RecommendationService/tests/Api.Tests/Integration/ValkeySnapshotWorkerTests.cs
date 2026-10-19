using System.Text.Json;
using Dprental.RecommendationService.Application.DTOs;
using Dprental.RecommendationService.Domain.IRepositories;
using Dprental.RecommendationService.Domain.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace Dprental.RecommendationService.Api.Tests.Integration;

public sealed class ValkeySnapshotWorkerTests
{
    private sealed class TestValkeyConnectionProvider : IValkeyConnectionProvider
    {
        private readonly IConnectionMultiplexer _multiplexer;

        public TestValkeyConnectionProvider(IConnectionMultiplexer multiplexer)
        {
            _multiplexer = multiplexer;
        }

        public bool IsConnected => _multiplexer.IsConnected;

        public Task<IConnectionMultiplexer?> GetMultiplexerAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IConnectionMultiplexer?>(_multiplexer);

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<IConnectionMultiplexer?> TryGetValkeyAsync()
    {
        string[] candidates =
        [
            Environment.GetEnvironmentVariable("VALKEY_TEST_CONNECTION_STRING") ?? "127.0.0.1:56379",
            "127.0.0.1:6379"
        ];

        foreach (var endpoint in candidates)
        {
            try
            {
                var options = ConfigurationOptions.Parse(endpoint);
                options.AbortOnConnectFail = false;
                options.ConnectTimeout = 1000;
                options.SyncTimeout = 1000;
                var mux = await ConnectionMultiplexer.ConnectAsync(options);
                if (mux.IsConnected)
                {
                    return mux;
                }
            }
            catch
            {
                // Try next
            }
        }

        return null;
    }

    [Fact]
    public void Snapshot_MaintainsInt64PrecisionAndJsonContract()
    {
        var items = new List<GlobalPopularListingsSnapshotItem>
        {
            new("33333333-3333-4333-8333-333333333333", 9223372036854775807L)
        };

        var snapshot = new GlobalPopularListingsSnapshot(
            Version: 1,
            GeneratedAt: "2026-10-19T10:00:00Z",
            Items: items);

        string json = JsonSerializer.Serialize(snapshot);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("2026-10-19T10:00:00Z", doc.RootElement.GetProperty("generatedAt").GetString());

        var firstItem = doc.RootElement.GetProperty("items")[0];
        Assert.Equal("33333333-3333-4333-8333-333333333333", firstItem.GetProperty("equipmentListingId").GetString());
        Assert.Equal(9223372036854775807L, firstItem.GetProperty("score").GetInt64());
    }

    [Fact]
    public async Task Valkey_RejectsOlderSnapshot_AcceptsNewerSnapshot()
    {
        using var mux = await TryGetValkeyAsync();
        if (mux == null)
        {
            Assert.Fail("Required Valkey instance is not available on 127.0.0.1:56379 or 127.0.0.1:6379. Acceptance integration tests must fail clearly when required infrastructure is absent, rather than returning early.");
        }

        var provider = new TestValkeyConnectionProvider(mux);
        var repo = new ValkeyProjectionRepository(provider, NullLogger<ValkeyProjectionRepository>.Instance);

        var userId = Guid.NewGuid();
        string key = $"rec:v1:user:{userId:D}:equipment-types";

        string json1 = $$"""
        {
            "version": 1,
            "generatedAt": "2026-10-19T10:00:00Z",
            "items": [{"equipmentTypeId": "{{Guid.NewGuid()}}", "score": 10}]
        }
        """;

        string jsonOlder = $$"""
        {
            "version": 1,
            "generatedAt": "2026-10-19T09:00:00Z",
            "items": [{"equipmentTypeId": "{{Guid.NewGuid()}}", "score": 5}]
        }
        """;

        string jsonNewer = $$"""
        {
            "version": 1,
            "generatedAt": "2026-10-19T11:00:00Z",
            "items": [{"equipmentTypeId": "{{Guid.NewGuid()}}", "score": 20}]
        }
        """;

        // 1. Initial snapshot write
        bool success1 = await repo.SetUserEquipmentTypesSnapshotAsync(userId, json1, "2026-10-19T10:00:00Z", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(success1);

        // 2. Older snapshot rejected by Lua script
        bool successOlder = await repo.SetUserEquipmentTypesSnapshotAsync(userId, jsonOlder, "2026-10-19T09:00:00Z", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.False(successOlder);

        // 3. Newer snapshot accepted
        bool successNewer = await repo.SetUserEquipmentTypesSnapshotAsync(userId, jsonNewer, "2026-10-19T11:00:00Z", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(successNewer);

        // Cleanup
        var db = mux.GetDatabase();
        await db.KeyDeleteAsync(key);
    }

    [Fact]
    public async Task ValkeyOutage_DoesNotThrow_ReturnsFalseSafely()
    {
        var provider = new NullValkeyConnectionProvider();
        var repo = new ValkeyProjectionRepository(provider, NullLogger<ValkeyProjectionRepository>.Instance);

        bool writeResult = await repo.SetGlobalPopularListingsSnapshotAsync("{}", "2026-10-19T10:00:00Z", TimeSpan.FromSeconds(300), CancellationToken.None);
        Assert.False(writeResult);

        string? readResult = await repo.GetGlobalPopularListingsSnapshotAsync(CancellationToken.None);
        Assert.Null(readResult);
    }
}
