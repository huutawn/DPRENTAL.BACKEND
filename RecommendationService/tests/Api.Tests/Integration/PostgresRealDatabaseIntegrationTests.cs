using Dprental.RecommendationService.Api.Tests.TestHelpers;
using Dprental.RecommendationService.Application.IServices;
using Dprental.RecommendationService.Application.DTOs;
using Dprental.RecommendationService.Application.Services;
using Dprental.RecommendationService.Application.Validation;
using Dprental.RecommendationService.Domain.IRepositories;
using Dprental.RecommendationService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Dprental.RecommendationService.Api.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class PostgresRealDatabaseIntegrationTests : IClassFixture<PostgreSqlTestcontainerFixture>
{
    private readonly PostgreSqlTestcontainerFixture _fixture;

    public PostgresRealDatabaseIntegrationTests(PostgreSqlTestcontainerFixture fixture)
    {
        _fixture = fixture;
    }

    private (InteractionIngestionService Service, InteractionUnitOfWork Uow, RecommendationServiceDbContext DbContext) CreateHarness()
    {
        var db = _fixture.CreateDbContext();
        var eventRepo = new InteractionEventRepository(db);
        var userInterestRepo = new UserEquipmentTypeInterestRepository(db);
        var listingPopularityRepo = new ListingPopularityRepository(db);
        var uow = new InteractionUnitOfWork(db);
        var validator = new EventSchemaValidator();

        var service = new InteractionIngestionService(
            validator,
            eventRepo,
            userInterestRepo,
            listingPopularityRepo,
            uow,
            Mock.Of<ISnapshotPublishingService>(),
            NullLogger<InteractionIngestionService>.Instance);

        return (service, uow, db);
    }

    [Fact]
    public async Task RealPostgres_FullIngestion_Duplicate_Conflict_AndRebuild_Lifecycle()
    {
        var (service, uow, db) = CreateHarness();
        await using (db)
        {
            await db.Database.MigrateAsync();

            var msgId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var listingId = Guid.NewGuid();
            var typeId = Guid.NewGuid();

            string viewJson = $$"""
            {
                "messageId": "{{msgId}}",
                "messageType": "ListingViewed",
                "version": 1,
                "producer": "InventoryService",
                "occurredAt": "2026-10-19T10:00:00Z",
                "payload": {
                    "userId": "{{userId}}",
                    "equipmentListingId": "{{listingId}}",
                    "equipmentTypeId": "{{typeId}}"
                }
            }
            """;

            // 1. First Ingestion: Processed
            var res1 = await service.ProcessEventAsync(viewJson, InteractionEventSource.Inventory, CancellationToken.None);
            Assert.Equal(EventProcessOutcome.Processed, res1.Outcome);

            var pop1 = await db.ListingPopularities.FindAsync(listingId);
            Assert.NotNull(pop1);
            Assert.Equal(2, pop1.Score);

            var user1 = await db.UserEquipmentTypeInterests.FindAsync(userId, typeId);
            Assert.NotNull(user1);
            Assert.Equal(2, user1.Score);

            // 2. Sequential Duplicate: DuplicateIgnored
            var res2 = await service.ProcessEventAsync(viewJson, InteractionEventSource.Inventory, CancellationToken.None);
            Assert.Equal(EventProcessOutcome.DuplicateIgnored, res2.Outcome);

            // Verify scores remain unchanged
            db.ChangeTracker.Clear();
            var pop2 = await db.ListingPopularities.FindAsync(listingId);
            Assert.NotNull(pop2);
            Assert.Equal(2, pop2.Score);

            // 3. Sequential Conflict (same messageId, differing payload): ConflictMismatch
            string conflictJson = $$"""
            {
                "messageId": "{{msgId}}",
                "messageType": "ListingViewed",
                "version": 1,
                "producer": "InventoryService",
                "occurredAt": "2026-10-19T10:00:00Z",
                "payload": {
                    "userId": "{{userId}}",
                    "equipmentListingId": "{{Guid.NewGuid()}}",
                    "equipmentTypeId": "{{typeId}}"
                }
            }
            """;

            var res3 = await service.ProcessEventAsync(conflictJson, InteractionEventSource.Inventory, CancellationToken.None);
            Assert.Equal(EventProcessOutcome.ConflictMismatch, res3.Outcome);

            // 4. Sequential CartItemAdded (+5)
            string cartJson = $$"""
            {
                "messageId": "{{Guid.NewGuid()}}",
                "messageType": "CartItemAdded",
                "version": 1,
                "producer": "RentalService",
                "occurredAt": "2026-10-19T10:05:00Z",
                "payload": {
                    "userId": "{{userId}}",
                    "equipmentListingId": "{{listingId}}",
                    "equipmentTypeId": "{{typeId}}",
                    "cartId": "{{Guid.NewGuid()}}",
                    "cartItemId": "{{Guid.NewGuid()}}"
                }
            }
            """;

            var res4 = await service.ProcessEventAsync(cartJson, InteractionEventSource.Rental, CancellationToken.None);
            Assert.Equal(EventProcessOutcome.Processed, res4.Outcome);

            db.ChangeTracker.Clear();
            var pop4 = await db.ListingPopularities.FindAsync(listingId);
            Assert.Equal(7, pop4!.Score); // 2 + 5

            var user4 = await db.UserEquipmentTypeInterests.FindAsync(userId, typeId);
            Assert.Equal(7, user4!.Score); // 2 + 5

            // 5. Out of order event: earlier occurredAt does not rewind last_occurred_at
            string earlierJson = $$"""
            {
                "messageId": "{{Guid.NewGuid()}}",
                "messageType": "ListingViewed",
                "version": 1,
                "producer": "InventoryService",
                "occurredAt": "2026-10-19T08:00:00Z",
                "payload": {
                    "userId": "{{userId}}",
                    "equipmentListingId": "{{listingId}}",
                    "equipmentTypeId": "{{typeId}}"
                }
            }
            """;

            var res5 = await service.ProcessEventAsync(earlierJson, InteractionEventSource.Inventory, CancellationToken.None);
            Assert.Equal(EventProcessOutcome.Processed, res5.Outcome);

            db.ChangeTracker.Clear();
            var pop5 = await db.ListingPopularities.FindAsync(listingId);
            Assert.Equal(9, pop5!.Score); // 7 + 2
            Assert.True(pop5.LastOccurredAt >= DateTimeOffset.Parse("2026-10-19T10:05:00Z", System.Globalization.CultureInfo.InvariantCulture));

            // 6. Full Rebuild Projections
            await uow.RebuildProjectionsAsync(CancellationToken.None);

            db.ChangeTracker.Clear();
            var popRebuild = await db.ListingPopularities.FindAsync(listingId);
            Assert.NotNull(popRebuild);
            Assert.Equal(9, popRebuild.Score);

            var userRebuild = await db.UserEquipmentTypeInterests.FindAsync(userId, typeId);
            Assert.NotNull(userRebuild);
            Assert.Equal(9, userRebuild.Score);
        }
    }

    [Fact]
    public async Task RealPostgres_ProjectionFailure_RollsBackRawEvent()
    {
        var db = _fixture.CreateDbContext();
        await using (db)
        {
            await db.Database.MigrateAsync();

            var eventRepo = new InteractionEventRepository(db);
            var listingPopularityRepo = new ListingPopularityRepository(db);
            var uow = new InteractionUnitOfWork(db);

            // Mock user interest repo that throws during upsert
            var failingUserRepoMock = new Mock<IUserEquipmentTypeInterestRepository>();
            failingUserRepoMock
                .Setup(r => r.UpsertAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<short>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Simulated database failure during projection upsert."));

            var service = new InteractionIngestionService(
                new EventSchemaValidator(),
                eventRepo,
                failingUserRepoMock.Object,
                listingPopularityRepo,
                uow,
                Mock.Of<ISnapshotPublishingService>(),
                NullLogger<InteractionIngestionService>.Instance);

            var msgId = Guid.NewGuid();
            string json = $$"""
            {
                "messageId": "{{msgId}}",
                "messageType": "ListingViewed",
                "version": 1,
                "producer": "InventoryService",
                "occurredAt": "2026-10-19T10:00:00Z",
                "payload": {
                    "userId": "{{Guid.NewGuid()}}",
                    "equipmentListingId": "{{Guid.NewGuid()}}",
                    "equipmentTypeId": "{{Guid.NewGuid()}}"
                }
            }
            """;

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await service.ProcessEventAsync(json, InteractionEventSource.Inventory, CancellationToken.None));

            // Verify raw event is NOT in database (transaction rolled back)
            db.ChangeTracker.Clear();
            var rawEvent = await db.InteractionEvents.FindAsync(msgId);
            Assert.Null(rawEvent);
        }
    }

    [Fact]
    public async Task RealPostgres_ConcurrentDuplicates_OnlyOneInserts_OthersIgnored()
    {
        var (service, _, db) = CreateHarness();
        await using (db)
        {
            await db.Database.MigrateAsync();

            var msgId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var listingId = Guid.NewGuid();
            var typeId = Guid.NewGuid();

            string json = $$"""
            {
                "messageId": "{{msgId}}",
                "messageType": "ListingViewed",
                "version": 1,
                "producer": "InventoryService",
                "occurredAt": "2026-10-19T10:00:00Z",
                "payload": {
                    "userId": "{{userId}}",
                    "equipmentListingId": "{{listingId}}",
                    "equipmentTypeId": "{{typeId}}"
                }
            }
            """;

            const int concurrency = 5;
            var barrier = new Barrier(concurrency);
            var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
            {
                var (s, _, ctx) = CreateHarness();
                await using (ctx)
                {
                    barrier.SignalAndWait(5000);
                    return await s.ProcessEventAsync(json, InteractionEventSource.Inventory, CancellationToken.None);
                }
            })).ToList();

            var results = await Task.WhenAll(tasks);

            int processedCount = results.Count(r => r.Outcome == EventProcessOutcome.Processed);
            int duplicateCount = results.Count(r => r.Outcome == EventProcessOutcome.DuplicateIgnored);

            Assert.Equal(1, processedCount);
            Assert.Equal(concurrency - 1, duplicateCount);

            db.ChangeTracker.Clear();
            var pop = await db.ListingPopularities.FindAsync(listingId);
            Assert.NotNull(pop);
            Assert.Equal(2, pop.Score);
        }
    }

    [Fact]
    public async Task RealPostgres_ConcurrentDistinctEvents_NoLostUpdates()
    {
        var (service, _, db) = CreateHarness();
        await using (db)
        {
            await db.Database.MigrateAsync();

            var listingId = Guid.NewGuid();
            var typeId = Guid.NewGuid();

            const int concurrency = 5;
            var barrier = new Barrier(concurrency);

            var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
            {
                var (s, _, ctx) = CreateHarness();
                await using (ctx)
                {
                    string json = $$"""
                    {
                        "messageId": "{{Guid.NewGuid()}}",
                        "messageType": "ListingViewed",
                        "version": 1,
                        "producer": "InventoryService",
                        "occurredAt": "2026-10-19T10:00:00Z",
                        "payload": {
                            "userId": null,
                            "equipmentListingId": "{{listingId}}",
                            "equipmentTypeId": "{{typeId}}"
                        }
                    }
                    """;

                    barrier.SignalAndWait(5000);
                    return await s.ProcessEventAsync(json, InteractionEventSource.Inventory, CancellationToken.None);
                }
            })).ToList();

            var results = await Task.WhenAll(tasks);
            Assert.All(results, r => Assert.Equal(EventProcessOutcome.Processed, r.Outcome));

            db.ChangeTracker.Clear();
            var pop = await db.ListingPopularities.FindAsync(listingId);
            Assert.NotNull(pop);
            Assert.Equal(concurrency * 2, pop.Score);
        }
    }

    [Fact]
    public async Task RealPostgres_CursorPagination_And_TieBreakDescending_Verified()
    {
        var db = _fixture.CreateDbContext();
        await using (db)
        {
            await db.Database.MigrateAsync();

            var userRepo = new UserEquipmentTypeInterestRepository(db);
            var listingRepo = new ListingPopularityRepository(db);

            // 1. Verify tie-break for ListingPopularity (score DESC, listingId DESC)
            var listingA = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var listingB = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var now = DateTimeOffset.UtcNow;

            await listingRepo.UpsertAsync(listingA, 10, now, CancellationToken.None);
            await listingRepo.UpsertAsync(listingB, 10, now, CancellationToken.None);

            var topListings = await listingRepo.GetTopPopularListingsAsync(10, CancellationToken.None);
            var targetListings = topListings.Where(x => x.EquipmentListingId == listingA || x.EquipmentListingId == listingB).ToList();
            Assert.Equal(2, targetListings.Count);
            // Both have score 10; tie-break DESC puts listingB before listingA
            Assert.Equal(listingB, targetListings[0].EquipmentListingId);
            Assert.Equal(listingA, targetListings[1].EquipmentListingId);

            // 2. Verify tie-break for UserEquipmentTypeInterest (score DESC, typeId DESC)
            var userId = Guid.NewGuid();
            var typeA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var typeB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

            await userRepo.UpsertAsync(userId, typeA, 5, now, CancellationToken.None);
            await userRepo.UpsertAsync(userId, typeB, 5, now, CancellationToken.None);

            var topTypes = await userRepo.GetTopInterestsAsync(userId, 10, CancellationToken.None);
            Assert.Equal(2, topTypes.Count);
            // Both have score 5; tie-break DESC puts typeB before typeA
            Assert.Equal(typeB, topTypes[0].EquipmentTypeId);
            Assert.Equal(typeA, topTypes[1].EquipmentTypeId);

            // 3. Verify cursor pagination with afterUserId
            var userIds = new List<Guid>();
            for (int i = 0; i < 5; i++)
            {
                var u = Guid.NewGuid();
                userIds.Add(u);
                await userRepo.UpsertAsync(u, Guid.NewGuid(), 1, now, CancellationToken.None);
            }

            userIds.Sort();

            // Page 1: take 2
            var page1 = await userRepo.GetDistinctUserIdsAsync(null, 2, CancellationToken.None);
            Assert.True(page1.Count <= 2);
            Assert.True(page1.SequenceEqual(page1.OrderBy(id => id)));

            // Page 2: after page1.Last()
            var cursor = page1[^1];
            var page2 = await userRepo.GetDistinctUserIdsAsync(cursor, 2, CancellationToken.None);
            Assert.All(page2, id => Assert.True(id.CompareTo(cursor) > 0));
        }
    }
}
