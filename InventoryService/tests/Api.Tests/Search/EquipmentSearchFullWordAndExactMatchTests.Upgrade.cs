using Dprental.InventoryService.Application.Search;
using Dprental.InventoryService.Application.Search.Adapters;
using Dprental.InventoryService.Application.Search.Models;
using Dprental.InventoryService.Application.Search.Workers;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Search;

public sealed partial class EquipmentSearchFullWordAndExactMatchTests
{
    private const string BeforeNameUpgrade = "20261023072732_AddBehaviorOutboxCleanupIndex";
    private const string NameUpgrade = "20261026100000_RebuildSearchForAssetNames";

    private InventoryServiceDbContext CreateMigrationDb() => new(
        new DbContextOptionsBuilder<InventoryServiceDbContext>().UseNpgsql(_postgres.ConnectionString,
            o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName,
                InventoryServiceDbContext.SchemaName)).Options);

    [Fact]
    public async Task Upgrade_LegacyReadyIndex_BootstrapRebuildsNames_AndRestartDoesNotRebuildAgain()
    {
        await using var migrationDb = CreateMigrationDb();
        var migrator = migrationDb.GetService<IMigrator>();
        await migrator.MigrateAsync(BeforeNameUpgrade);

        var index = _client.Index(_options.OwnerAssetsIndex);
        var deletion = await index.DeleteAllDocumentsAsync();
        await _client.WaitForTaskAsync(deletion.TaskUid);
        var upload = await index.AddDocumentsAsync(new[]
        {
            new
            {
                id = _assetXeNang.Id.ToString("D"), ownerUserId = _owner1Id.ToString("D"),
                equipmentListingId = _listingXeNang.Id.ToString("D"),
                serialNumber = _assetXeNang.SerialNumber, model = _listingXeNang.Model
            }
        }, "id");
        await _client.WaitForTaskAsync(upload.TaskUid);
        Assert.True((await _store.GetGenerationAsync())!.IsReady);
        var before = await _searchService.SearchOwnerAssetCandidatesAsync(new(_owner1Id, "xe nâng"));
        Assert.True(before.IsSuccess);
        Assert.Empty(before.Value.CandidateIds);

        await migrator.MigrateAsync(NameUpgrade);
        Assert.False((await _store.GetGenerationAsync())!.IsReady);
        Assert.Equal("SEARCH_NOT_READY", (await _searchService.SearchOwnerAssetCandidatesAsync(new(_owner1Id, "xe nâng"))).Error.Code);

        // The current application model requires every migration after the upgrade under test.
        await migrator.MigrateAsync();

        var services = new ServiceCollection();
        services.AddDbContext<InventoryServiceDbContext>(o => o.UseNpgsql(_postgres.ConnectionString));
        services.AddScoped<ISearchProjectionStore, SearchProjectionStore>();
        services.AddSingleton<IInventoryIndexProvisioner>(_provisioner);
        services.AddSingleton<IInventorySearchBackfillService>(sp => new MeilisearchBackfillService(
            sp.GetRequiredService<IServiceScopeFactory>(), _client, Options.Create(_options), TimeProvider.System));
        await using var provider = services.BuildServiceProvider();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using (var bootstrap = CreateBootstrap(provider))
        {
            Assert.True(await bootstrap.SynchronizeAsync(timeout.Token));
        }

        var document = await index.GetDocumentAsync<OwnerAssetIndexDocument>(_assetXeNang.Id.ToString("D"));
        Assert.Equal("Xe nâng", document.Name);
        var secondDocument = await index.GetDocumentAsync<OwnerAssetIndexDocument>(_assetXeLuAbc.Id.ToString("D"));
        Assert.Equal("Xe lu ABC", secondDocument.Name);
        var after = await _searchService.SearchOwnerAssetCandidatesAsync(new(_owner1Id, "xe nâng"));
        Assert.True(after.IsSuccess);
        Assert.Contains(_assetXeNang.Id, after.Value.CandidateIds);
        int runCount = await _db.SearchBackfillRuns.CountAsync();
        Assert.Equal(1, runCount);

        using (var restarted = CreateBootstrap(provider))
        {
            Assert.True(await restarted.SynchronizeAsync(timeout.Token));
        }
        await migrator.MigrateAsync();
        Assert.True((await _store.GetGenerationAsync())!.IsReady);
        Assert.Equal(runCount, await _db.SearchBackfillRuns.CountAsync());
    }

    [Theory]
    [InlineData(SearchBackfillRun.StatusBuilding)]
    [InlineData(SearchBackfillRun.StatusCatchingUp)]
    [InlineData(SearchBackfillRun.StatusSwapping)]
    [InlineData("UnknownGenerationSubmission")]
    [InlineData("UnknownJobSubmission")]
    public async Task Upgrade_UnresolvedSearchWork_RejectsMigrationWithoutChangingReadiness(string state)
    {
        await using var migrationDb = CreateMigrationDb();
        var migrator = migrationDb.GetService<IMigrator>();
        await migrator.MigrateAsync(BeforeNameUpgrade);
        if (state == "UnknownGenerationSubmission")
        {
            var generation = (await _store.GetGenerationAsync())!;
            generation.MarkUnavailable("SEARCH_SUBMISSION_UNKNOWN", DateTimeOffset.UtcNow);
            await _store.SaveGenerationAsync(generation);
        }
        else if (state == "UnknownJobSubmission")
        {
            var now = DateTimeOffset.UtcNow;
            await _store.UpsertJobAsync("InventoryAsset", _assetXeNang.Id, "Upsert", 1, false, now);
            var lease = Guid.NewGuid();
            var job = Assert.Single(await _store.ClaimPendingBatchAsync(1, lease, TimeSpan.FromMinutes(1), now));
            Assert.True(await _store.SaveSubmissionAsync(job.Id, lease, 1, [], true, false, now));
        }
        else
        {
            var run = SearchBackfillRun.Create(Guid.NewGuid(), "staging_public", "staging_listings", "staging_assets", null, DateTimeOffset.UtcNow);
            run.Transition(state, DateTimeOffset.UtcNow);
            await _store.SaveBackfillRunAsync(run);
        }

        var error = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(NameUpgrade));
        Assert.Contains("Finish or reconcile", error.MessageText, StringComparison.Ordinal);
        Assert.DoesNotContain(NameUpgrade, await migrationDb.Database.GetAppliedMigrationsAsync());
        var saved = (await _store.GetGenerationAsync())!;
        Assert.Equal(state != "UnknownGenerationSubmission", saved.IsReady);
        if (state == "UnknownGenerationSubmission") Assert.Equal("SEARCH_SUBMISSION_UNKNOWN", saved.ErrorCode);
    }

    private SearchBootstrapHostedService CreateBootstrap(ServiceProvider provider) => new(
        provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(_options),
        NullLogger<SearchBootstrapHostedService>.Instance);
}
