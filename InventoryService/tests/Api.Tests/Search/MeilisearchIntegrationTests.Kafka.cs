using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Kafka.Options;
using Dprental.InventoryService.Application.Kafka.Outbox;
using Dprental.InventoryService.Application.Kafka.Transport;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Search;

public sealed partial class MeilisearchIntegrationTests
{
    [Fact]
    public async Task RealKafka_OutboxToInboxJobToWorker_ReachesSearchIndex()
    {
        const string broker = "127.0.0.1:29092";
        var options = Options.Create(new KafkaOptions { BootstrapServers = broker, ClientId = "search-integration", DeliveryTimeoutMs = 5000 });
        var topic = $"inventory-search-test-{Guid.NewGuid():N}";
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = broker }).Build();
        await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = 1, ReplicationFactor = 1 }]);
        var registry = new KafkaHandlerRegistry().Register<SearchProjectionMessageHandler, SearchProjectionChangedPayload>(
            topic, SearchProjectionEvents.ListingMessageType, 1,
            "search-projection-test", $"search-test-{Guid.NewGuid():N}");
        var collection = new ServiceCollection().AddLogging();
        collection.AddInventoryServiceDomainRepositories(_postgres.ConnectionString);
        collection.AddScoped<SearchProjectionMessageHandler>();
        await using var services = collection.BuildServiceProvider();
        using var producer = new KafkaProducer(options);
        using var consumer = new KafkaConsumerWorker(services.GetRequiredService<IServiceScopeFactory>(), registry, options, producer);
        var listing = NewListing("Kafka camera unique", db: _db);
        _db.EquipmentListings.Add(listing);
        var source = SearchProjectionEvents.CreateListingProjectionOutboxMessage(listing.Id, "Upsert", 1, false, DateTimeOffset.UtcNow);
        // Isolate broker traffic while keeping the production envelope, publisher, consumer and handler unchanged.
        var message = OutboxMessage.Create(source.Id, topic, source.KafkaKey, source.Payload, source.CreatedAt);
        _db.OutboxMessages.Add(message);
        await _db.SaveChangesAsync();
        await consumer.StartAsync(default);
        try
        {
            using var publisher = new OutboxPublisherWorker(services.GetRequiredService<IServiceScopeFactory>(), producer);
            Assert.Equal(1, await publisher.ProcessBatchAsync());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!await _db.SearchProjectionJobs.AnyAsync(j => j.EntityId == listing.Id, deadline.Token))
                await Task.Delay(100, deadline.Token);
            var inbox = await _db.InboxMessages.AsNoTracking().SingleAsync(i => i.MessageId == message.Id.ToString("D"), deadline.Token);
            Assert.Equal(Dprental.InventoryService.Common.Enums.InboxMessageStatus.Processed, inbox.Status);
            Assert.NotNull((await _db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == message.Id, deadline.Token)).PublishedAt);
            Assert.Equal(1, await CreateWorker(services).ProcessBatchAsync(deadline.Token));
            Assert.Contains(listing.Id, (await _searchService.SearchPublicListingCandidatesAsync(new("Kafka camera"), deadline.Token)).Value.CandidateIds);
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await consumer.StopAsync(stop.Token);
            await admin.DeleteTopicsAsync([topic]);
        }
    }

    [Fact]
    public async Task UnknownLiveSubmission_AfterCrash_QuarantinesGenerationBeforeAnyFurtherWrites()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _store.UpsertJobAsync("InventoryAsset", Guid.NewGuid(), "Upsert", 1, false, now);
        var token = Guid.NewGuid();
        var job = Assert.Single(await _store.ClaimPendingBatchAsync(1, token, TimeSpan.FromSeconds(30), now));
        Assert.True(await _store.SaveSubmissionAsync(job.Id, token, 1, [], true, false, now));
        await using var services = BuildSearchProvider();
        Assert.Equal(0, await CreateWorker(services).ProcessBatchAsync());
        var generation = (await _store.GetGenerationAsync())!;
        Assert.False(generation.IsReady);
        Assert.Equal("SEARCH_SUBMISSION_UNKNOWN", generation.ErrorCode);
        var saved = await _db.SearchProjectionJobs.AsNoTracking().SingleAsync();
        Assert.True(saved.SubmissionUncertain);
        Assert.Equal(0, saved.AppliedRevision);
    }
}
