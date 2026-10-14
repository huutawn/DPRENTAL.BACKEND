using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Kafka.Transport;
using Dprental.InventoryService.Application.Search;
using Dprental.InventoryService.Application.Search.Adapters;
using Dprental.InventoryService.Application.Search.DependencyInjection;
using Dprental.InventoryService.Application.Search.Workers;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Meilisearch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Search;

public sealed partial class MeilisearchIntegrationTests
{
    private InventoryServiceDbContext CreateDb() => new(new DbContextOptionsBuilder<InventoryServiceDbContext>()
        .UseNpgsql(_postgres.ConnectionString).Options);

    [Fact]
    public async Task Reclaim_PreservesSubmittedRevision_AndCannotAcknowledgeNewerDesiredRevision()
    {
        var now = DateTimeOffset.UtcNow;
        var entity = Guid.NewGuid();
        await _store.UpsertJobAsync("InventoryAsset", entity, "Upsert", 10, false, now);
        var firstLease = Guid.NewGuid();
        var original = Assert.Single(await _store.ClaimPendingBatchAsync(1, firstLease, TimeSpan.FromSeconds(30), now));
        Assert.True(await _store.SaveSubmissionAsync(original.Id, firstLease, 10, [42], false, true, now));
        await using var otherDb = CreateDb();
        var other = new SearchProjectionStore(otherDb);
        await other.UpsertJobAsync("InventoryAsset", entity, "Delete", 11, false, now);
        var secondLease = Guid.NewGuid();
        var reclaimed = Assert.Single(await other.ClaimPendingBatchAsync(1, secondLease, TimeSpan.FromSeconds(30), now.AddSeconds(31)));
        Assert.Equal(10, reclaimed.ProcessingRevision);
        Assert.Equal(11, reclaimed.DesiredRevision);
        Assert.Equal([42], reclaimed.PendingTaskUids);
        Assert.False(await _store.MarkCompletedAsync(original.Id, firstLease, 10, now.AddSeconds(32)));
        Assert.False(await other.MarkCompletedAsync(original.Id, secondLease, 11, now.AddSeconds(32)));
        Assert.True(await other.MarkCompletedAsync(original.Id, secondLease, 10, now.AddSeconds(32)));
        var saved = await otherDb.SearchProjectionJobs.AsNoTracking().SingleAsync();
        Assert.Equal(10, saved.AppliedRevision);
        Assert.Equal(SearchProjectionJob.StatusPending, saved.Status);
        Assert.Equal(11, Assert.Single(await other.ClaimPendingBatchAsync(1, Guid.NewGuid(), TimeSpan.FromSeconds(30), now.AddSeconds(33))).ProcessingRevision);
    }

    [Fact]
    public async Task ExpiredLease_CannotRenewOrPersistSubmission_AndClaimIsExclusive()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.UpsertJobAsync("InventoryAsset", Guid.NewGuid(), "Upsert", 1, false, now);
        var token = Guid.NewGuid();
        var job = Assert.Single(await _store.ClaimPendingBatchAsync(1, token, TimeSpan.FromSeconds(30), now));
        await using var db = CreateDb();
        var second = new SearchProjectionStore(db);
        Assert.Empty(await second.ClaimPendingBatchAsync(1, Guid.NewGuid(), TimeSpan.FromSeconds(30), now));
        Assert.False(await _store.RenewLeaseAsync(job.Id, token, TimeSpan.FromSeconds(30), now.AddSeconds(31)));
        Assert.False(await _store.SaveSubmissionAsync(job.Id, token, 1, [], false, true, now.AddSeconds(31)));
    }

    [Fact]
    public async Task DuplicateRevision_DoesNotResetRetryOrFanoutProgress()
    {
        var now = DateTimeOffset.UtcNow;
        var entity = Guid.NewGuid();
        await _store.UpsertJobAsync("EquipmentListing", entity, "Upsert", 10, true, now);
        var token = Guid.NewGuid();
        var job = Assert.Single(await _store.ClaimPendingBatchAsync(1, token, TimeSpan.FromSeconds(30), now));
        var cursor = Guid.NewGuid();
        Assert.True(await _store.SaveFanoutProgressAsync(job.Id, token, 10, cursor, false, now));
        Assert.True(await _store.MarkFailedAsync(job.Id, token, 10, "test failure", now.AddSeconds(5), now));
        await _store.UpsertJobAsync("EquipmentListing", entity, "Upsert", 10, true, now);
        var saved = await _db.SearchProjectionJobs.AsNoTracking().SingleAsync();
        Assert.Equal(1, saved.RetryCount);
        Assert.Equal(cursor, saved.FanoutCursor);
        await _store.UpsertJobAsync("EquipmentListing", entity, "Upsert", 11, true, now);
        var next = Assert.Single(await _store.ClaimPendingBatchAsync(1, Guid.NewGuid(), TimeSpan.FromSeconds(30), now));
        Assert.Null(next.FanoutCursor);
        Assert.Equal(11, next.FanoutRevision);
    }

    [Fact]
    public async Task WriterLock_IsExclusiveBetweenConnections_AndDoesNotLeaseLosingReplicaJobs()
    {
        await using var db = CreateDb();
        await using var writer = await SearchCoordination.TryAcquireAsync(db, SearchCoordination.WriterKey, default);
        Assert.NotNull(writer);
        await _store.UpsertJobAsync("InventoryAsset", Guid.NewGuid(), "Delete", 1, false, DateTimeOffset.UtcNow);
        await using var services = BuildSearchProvider();
        var worker = CreateWorker(services);
        Assert.Equal(0, await worker.ProcessBatchAsync());
        var job = await _db.SearchProjectionJobs.AsNoTracking().SingleAsync();
        Assert.Equal(SearchProjectionJob.StatusPending, job.Status);
        Assert.Null(job.LockLeaseToken);
    }

    [Fact]
    public async Task ReadinessAndCredentials_AreExplicit_AndCallerCancellationPropagates()
    {
        var generation = (await _store.GetGenerationAsync())!;
        generation.MarkUnavailable(null, DateTimeOffset.UtcNow);
        await _store.SaveGenerationAsync(generation);
        Assert.Equal("SEARCH_NOT_READY", (await _searchService.SearchPublicListingCandidatesAsync(new("camera"))).Error.Code);
        Assert.Equal("SEARCH_NOT_READY", (await _searchService.SearchOwnerListingCandidatesAsync(new(Guid.NewGuid(), "camera"))).Error.Code);
        Assert.Equal("SEARCH_NOT_READY", (await _searchService.SearchOwnerAssetCandidatesAsync(new(Guid.NewGuid(), "camera"))).Error.Code);
        generation.MarkReady(DateTimeOffset.UtcNow);
        await _store.SaveGenerationAsync(generation);
        var denied = new MeilisearchInventoryIndexService(new MeilisearchClient(MeiliUrl, "invalid-key"), _store,
            Options.Create(_options), NullLogger<MeilisearchInventoryIndexService>.Instance);
        Assert.Equal("SEARCH_CREDENTIALS_INVALID", (await denied.SearchPublicListingCandidatesAsync(new("camera"))).Error.Code);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _searchService.SearchPublicListingCandidatesAsync(new("camera"), cancelled.Token));
    }

    [Theory]
    [InlineData("Other", "Upsert", 1)]
    [InlineData("EquipmentListing", "Unknown", 1)]
    [InlineData("InventoryAsset", "Upsert", 0)]
    public async Task Consumer_RejectsInvalidPayload_WithoutCreatingJobs(string type, string action, long revision)
    {
        var payload = new SearchProjectionChangedPayload(type, Guid.NewGuid(), action, revision, DateTimeOffset.UtcNow);
        var result = await new SearchProjectionMessageHandler(_store).HandleAsync(
            MessageEnvelope.CreateRoot("InventoryListingProjectionChanged", payload.EntityId.ToString(), payload), default);
        Assert.True(result.IsFailure);
        Assert.Empty(await _db.SearchProjectionJobs.ToListAsync());
    }

    [Fact]
    public void NamedHttpClient_UsesConfiguredTimeout()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Meilisearch:Url"] = MeiliUrl, ["Meilisearch:ApiKey"] = MasterKey, ["Meilisearch:TimeoutSeconds"] = "2" }).Build();
        var collection = new ServiceCollection().AddLogging().AddInventoryMeilisearch(config);
        using var services = collection.BuildServiceProvider();
        using var http = services.GetRequiredService<IHttpClientFactory>().CreateClient("InventoryMeilisearch");
        Assert.Equal(TimeSpan.FromSeconds(2), http.Timeout);
    }

    [Fact]
    public async Task ReplayReceipts_DoNotSkipTransactionsCommittedOutOfOrder()
    {
        var run = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var lateDb = CreateDb();
        await using var lateTransaction = await lateDb.Database.BeginTransactionAsync();
        var late = Dprental.InventoryService.Application.Kafka.Outbox.SearchProjectionEvents.CreateAssetProjectionOutboxMessage(
            Guid.NewGuid(), "Upsert", 1, now.AddSeconds(-10), run);
        lateDb.OutboxMessages.Add(late);
        await lateDb.SaveChangesAsync();
        var early = Dprental.InventoryService.Application.Kafka.Outbox.SearchProjectionEvents.CreateAssetProjectionOutboxMessage(
            Guid.NewGuid(), "Upsert", 2, now, run);
        _db.OutboxMessages.Add(early);
        await _db.SaveChangesAsync();
        Assert.Equal(early.Id, Assert.Single(await _store.ReadBackfillJournalAsync(run, KafkaTopics.InventoryProjectionEventsV1, 1000)).Id);
        await _store.RecordBackfillReceiptsAsync(run, [early.Id]);
        await lateTransaction.CommitAsync();
        Assert.Equal(late.Id, Assert.Single(await _store.ReadBackfillJournalAsync(run, KafkaTopics.InventoryProjectionEventsV1, 1000)).Id);
        await _store.RecordBackfillReceiptsAsync(run, [late.Id]);
        Assert.Empty(await _store.ReadBackfillJournalAsync(run, KafkaTopics.InventoryProjectionEventsV1, 1000));
    }

    [Fact]
    public async Task MetadataWrite_WaitsForCutoverBarrier_AndTagsTheCommittedRun()
    {
        var collection = new ServiceCollection().AddLogging();
        collection.AddInventoryServiceDomainRepositories(_postgres.ConnectionString);
        await using var services = collection.BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var writes = scope.ServiceProvider.GetRequiredService<IInventoryWriteTransaction>();
        var barrier = (await _store.TryAcquireCoordinationAsync(SearchCoordination.MutationBarrierKey))!;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = writes.ExecuteListingWriteAsync<string>(Guid.NewGuid(), async (context, ct) =>
        {
            reached.SetResult();
            await context.GetNextSearchRevisionAsync(ct);
            return Dprental.InventoryService.Common.Result.Success(context.SearchBackfillRunId!.Value.ToString("D"));
        }, deadline.Token);
        Guid run = Guid.NewGuid();
        try
        {
            await reached.Task.WaitAsync(deadline.Token);
            await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromMilliseconds(100)));
            var generation = (await _store.GetGenerationAsync())!;
            generation.SetActiveRun(run, DateTimeOffset.UtcNow);
            await _store.SaveGenerationAsync(generation);
        }
        finally { await barrier.DisposeAsync(); }
        var result = await pending;
        Assert.True(result.IsSuccess);
        Assert.Equal(run.ToString("D"), result.Value);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task ServerErrorsWithoutJson_RemainTransientForSqlFallback(int status)
    {
        using var handler = new ServerErrorHandler((System.Net.HttpStatusCode)status);
        using var http = new HttpClient(handler) { BaseAddress = new Uri(MeiliUrl) };
        var service = new MeilisearchInventoryIndexService(new MeilisearchClient(http, MasterKey), _store,
            Options.Create(_options), NullLogger<MeilisearchInventoryIndexService>.Instance);
        var result = await service.SearchPublicListingCandidatesAsync(new("camera"));
        Assert.Equal("MEILISEARCH_QUERY_FAILED", result.Error.Code);
    }

    private sealed class ServerErrorHandler(System.Net.HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(string.Empty) });
    }

    [Fact]
    public async Task OutOfOrderMetadataEvent_PreservesFanoutWithoutRegressingDesiredState()
    {
        var now = DateTimeOffset.UtcNow;
        var listing = Guid.NewGuid();
        await _store.UpsertJobAsync("EquipmentListing", listing, "Upsert", 11, false, now);
        var token = Guid.NewGuid();
        var failed = Assert.Single(await _store.ClaimPendingBatchAsync(1, token, TimeSpan.FromSeconds(30), now));
        Assert.True(await _store.MarkFailedAsync(failed.Id, token, 11, "test failure", now.AddSeconds(60), now));
        await _store.UpsertJobAsync("EquipmentListing", listing, "Delete", 10, true, now);
        var job = Assert.Single(await _store.ClaimPendingBatchAsync(1, Guid.NewGuid(), TimeSpan.FromSeconds(30), now));
        Assert.Equal(11, job.DesiredRevision);
        Assert.Equal(0, job.RetryCount);
        Assert.Equal("Upsert", job.Action);
        Assert.Equal(10, job.FanoutRequestedRevision);
        Assert.Equal(11, job.FanoutRevision);
        Assert.False(job.FanoutCompleted);
    }

    private ServiceProvider BuildSearchProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<InventoryServiceDbContext>(o => o.UseNpgsql(_postgres.ConnectionString));
        services.AddScoped<ISearchProjectionStore, SearchProjectionStore>();
        services.AddSingleton<IInventoryIndexProvisioner>(_provisioner);
        return services.BuildServiceProvider();
    }

    private SearchProjectionWorker CreateWorker(ServiceProvider provider) => new(
        provider.GetRequiredService<IServiceScopeFactory>(), _client, Options.Create(_options),
        TimeProvider.System, NullLogger<SearchProjectionWorker>.Instance);
}
