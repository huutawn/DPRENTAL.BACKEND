using Dprental.RecommendationService.Api.Tests.TestHelpers;
using Dprental.RecommendationService.Domain.Entities;
using Dprental.RecommendationService.Domain.IRepositories;
using Dprental.RecommendationService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.RecommendationService.Api.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class PostgresRepositorySimplificationTests : IClassFixture<PostgreSqlTestcontainerFixture>
{
    private readonly PostgreSqlTestcontainerFixture _fixture;

    public PostgresRepositorySimplificationTests(PostgreSqlTestcontainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void UserPagination_GeneratedSql_OnlySelectsUserId_WithDistinctOrderingAndLimit()
    {
        var db = _fixture.CreateDbContext();
        var cursor = Guid.NewGuid();

        var queryWithCursor = db.UserEquipmentTypeInterests
            .AsNoTracking()
            .Where(x => x.UserId.CompareTo(cursor) > 0)
            .Select(x => x.UserId)
            .Distinct()
            .OrderBy(u => u)
            .Take(10);

        var sqlWithCursor = queryWithCursor.ToQueryString();
        Assert.Contains("user_id", sqlWithCursor);
        Assert.Contains(">", sqlWithCursor);
        Assert.Contains("DISTINCT", sqlWithCursor, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", sqlWithCursor, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT", sqlWithCursor, StringComparison.OrdinalIgnoreCase);

        var queryWithoutCursor = db.UserEquipmentTypeInterests
            .AsNoTracking()
            .Select(x => x.UserId)
            .Distinct()
            .OrderBy(u => u)
            .Take(5);

        var sqlWithoutCursor = queryWithoutCursor.ToQueryString();
        Assert.Contains("user_id", sqlWithoutCursor);
        Assert.Contains("DISTINCT", sqlWithoutCursor, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", sqlWithoutCursor, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT", sqlWithoutCursor, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UserPagination_FixedUuids_MatchesPostgreSqlDirectExecutionAndOrdering()
    {
        var db = _fixture.CreateDbContext();
        await using (db)
        {
            await db.Database.MigrateAsync();
            var repo = new UserEquipmentTypeInterestRepository(db);

            var fixedUuids = new[]
            {
                Guid.Parse("11111111-0000-0000-0000-000000000001"),
                Guid.Parse("22222222-0000-0000-0000-000000000002"),
                Guid.Parse("33333333-0000-0000-0000-000000000003"),
                Guid.Parse("44444444-0000-0000-0000-000000000004"),
                Guid.Parse("55555555-0000-0000-0000-000000000005")
            };

            var now = DateTimeOffset.UtcNow;
            foreach (var uid in fixedUuids)
            {
                await repo.UpsertAsync(uid, Guid.NewGuid(), 1, now, CancellationToken.None);
            }

            var cursor = fixedUuids[1]; // 22222222-...

            var linqResult = await repo.GetDistinctUserIdsAsync(cursor, 10, CancellationToken.None);

            var directSqlResult = await db.Database
                .SqlQuery<Guid>($"SELECT DISTINCT user_id AS \"Value\" FROM recommendation.user_equipment_type_interest WHERE user_id > {cursor} ORDER BY user_id ASC LIMIT 10")
                .ToListAsync(CancellationToken.None);

            var relevantLinq = linqResult.Where(id => fixedUuids.Contains(id)).ToList();
            var relevantDirect = directSqlResult.Where(id => fixedUuids.Contains(id)).ToList();

            Assert.Equal(3, relevantLinq.Count);
            Assert.Equal(relevantDirect, relevantLinq);
            Assert.Equal(fixedUuids[2..], relevantLinq);
        }
    }

    [Fact]
    public async Task UserPagination_EdgeCases_EmptyData_NullCursor_TakeZero_BeyondEndCursor()
    {
        var db = _fixture.CreateDbContext();
        await using (db)
        {
            await db.Database.MigrateAsync();
            var repo = new UserEquipmentTypeInterestRepository(db);

            // 1. Cursor beyond all possible UUIDs
            var maxGuid = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
            var emptyResult = await repo.GetDistinctUserIdsAsync(maxGuid, 10, CancellationToken.None);
            Assert.Empty(emptyResult);

            // 2. Take = 0
            var zeroResult = await repo.GetDistinctUserIdsAsync(null, 0, CancellationToken.None);
            Assert.Empty(zeroResult);

            // 3. Cursor null on existing data returns first page
            var uidA = Guid.Parse("01000000-0000-0000-0000-000000000001");
            var uidB = Guid.Parse("01000000-0000-0000-0000-000000000002");
            var now = DateTimeOffset.UtcNow;

            await repo.UpsertAsync(uidA, Guid.NewGuid(), 1, now, CancellationToken.None);
            await repo.UpsertAsync(uidB, Guid.NewGuid(), 1, now, CancellationToken.None);

            var firstPage = await repo.GetDistinctUserIdsAsync(null, 1, CancellationToken.None);
            Assert.Single(firstPage);

            // 4. Non-existent cursor between items
            var nonExistentCursor = Guid.Parse("01000000-0000-0000-0000-000000000001");
            var pageAfterNonExistent = await repo.GetDistinctUserIdsAsync(nonExistentCursor, 10, CancellationToken.None);
            Assert.All(pageAfterNonExistent, id => Assert.True(id.CompareTo(nonExistentCursor) > 0));
        }
    }

    [Fact]
    public async Task UserPagination_OneUserMultipleEquipmentTypes_DistinctReturnsOnce()
    {
        var db = _fixture.CreateDbContext();
        await using (db)
        {
            await db.Database.MigrateAsync();
            var repo = new UserEquipmentTypeInterestRepository(db);

            var userId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            // Same user has 3 distinct equipment types
            await repo.UpsertAsync(userId, Guid.NewGuid(), 2, now, CancellationToken.None);
            await repo.UpsertAsync(userId, Guid.NewGuid(), 3, now, CancellationToken.None);
            await repo.UpsertAsync(userId, Guid.NewGuid(), 5, now, CancellationToken.None);

            var users = await repo.GetDistinctUserIdsAsync(null, 1000, CancellationToken.None);
            var matching = users.Where(id => id == userId).ToList();

            Assert.Single(matching);
        }
    }

    [Fact]
    public async Task UserPagination_MultiplePages_TraversesAllWithoutDuplicatesOrOmissions()
    {
        var db = _fixture.CreateDbContext();
        await using (db)
        {
            await db.Database.MigrateAsync();
            var repo = new UserEquipmentTypeInterestRepository(db);

            var basePrefix = Guid.NewGuid().ToString("N")[..8];
            var createdUsers = new List<Guid>();
            var now = DateTimeOffset.UtcNow;

            for (int i = 0; i < 7; i++)
            {
                var uid = Guid.Parse($"{basePrefix}-0000-0000-0000-{i:D12}");
                createdUsers.Add(uid);
                // Some users have multiple types
                await repo.UpsertAsync(uid, Guid.NewGuid(), 1, now, CancellationToken.None);
                if (i % 2 == 0)
                {
                    await repo.UpsertAsync(uid, Guid.NewGuid(), 2, now, CancellationToken.None);
                }
            }

            createdUsers.Sort();

            // Paginate through with take = 2
            var paginated = new List<Guid>();
            Guid? cursor = null;

            while (true)
            {
                var page = await repo.GetDistinctUserIdsAsync(cursor, 2, CancellationToken.None);
                if (page.Count == 0)
                {
                    break;
                }

                // Filter to our test batch to avoid interference from other tests
                var relevantPage = page.Where(id => createdUsers.Contains(id)).ToList();
                paginated.AddRange(relevantPage);

                cursor = page[^1];
            }

            Assert.Equal(createdUsers.Count, paginated.Count);
            Assert.Equal(createdUsers, paginated);
        }
    }

    [Fact]
    public async Task Ingestion_JsonbWhitespaceAndKeyOrderEquivalence_ReturnsDuplicateIgnored()
    {
        var db = _fixture.CreateDbContext();
        await using (db)
        {
            await db.Database.MigrateAsync();
            var repo = new InteractionEventRepository(db);

            var msgId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var listingId = Guid.NewGuid();
            var typeId = Guid.NewGuid();
            var event1 = new InteractionEvent
            {
                MessageId = msgId,
                MessageType = "ListingViewed",
                Version = 1,
                Producer = "InventoryService",
                OccurredAt = now,
                EquipmentListingId = listingId,
                EquipmentTypeId = typeId,
                ScoreWeight = 2,
                Envelope = "{\"b\":2,\"a\":1}"
            };

            var res1 = await repo.IngestAsync(event1, CancellationToken.None);
            Assert.Equal(InteractionIngestStatus.Inserted, res1.Status);

            // Different whitespace, newlines, and key order: semantically identical jsonb
            var event2 = new InteractionEvent
            {
                MessageId = msgId,
                MessageType = "ListingViewed",
                Version = 1,
                Producer = "InventoryService",
                OccurredAt = now,
                EquipmentListingId = listingId,
                EquipmentTypeId = typeId,
                ScoreWeight = 2,
                Envelope = "{\n  \"a\": 1,\n  \"b\": 2\n}"
            };

            var res2 = await repo.IngestAsync(event2, CancellationToken.None);
            Assert.Equal(InteractionIngestStatus.DuplicateIgnored, res2.Status);
        }
    }

    [Fact]
    public async Task Ingestion_DifferingPayload_ReturnsConflictMismatch()
    {
        var db = _fixture.CreateDbContext();
        await using (db)
        {
            await db.Database.MigrateAsync();
            var repo = new InteractionEventRepository(db);

            var msgId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var listingId = Guid.NewGuid();
            var typeId = Guid.NewGuid();

            var event1 = new InteractionEvent
            {
                MessageId = msgId,
                MessageType = "ListingViewed",
                Version = 1,
                Producer = "InventoryService",
                OccurredAt = now,
                EquipmentListingId = listingId,
                EquipmentTypeId = typeId,
                ScoreWeight = 2,
                Envelope = "{\"userId\":\"u1\",\"score\":10}"
            };

            var res1 = await repo.IngestAsync(event1, CancellationToken.None);
            Assert.Equal(InteractionIngestStatus.Inserted, res1.Status);

            var eventConflict = new InteractionEvent
            {
                MessageId = msgId,
                MessageType = "ListingViewed",
                Version = 1,
                Producer = "InventoryService",
                OccurredAt = now,
                EquipmentListingId = listingId,
                EquipmentTypeId = typeId,
                ScoreWeight = 2,
                Envelope = "{\"userId\":\"u2\",\"score\":99}"
            };

            var resConflict = await repo.IngestAsync(eventConflict, CancellationToken.None);
            Assert.Equal(InteractionIngestStatus.ConflictMismatch, resConflict.Status);
            Assert.NotNull(resConflict.ConflictReason);
            Assert.Contains(msgId.ToString(), resConflict.ConflictReason);
        }
    }

    [Fact]
    public async Task Ingestion_NullableFields_HandledProperly()
    {
        var db = _fixture.CreateDbContext();
        await using (db)
        {
            await db.Database.MigrateAsync();
            var repo = new InteractionEventRepository(db);

            var msgId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            var nullableEvent = new InteractionEvent
            {
                MessageId = msgId,
                MessageType = "SearchPerformed",
                Version = 1,
                Producer = "InventoryService",
                OccurredAt = now,
                CorrelationId = null,
                UserId = null,
                EquipmentListingId = null,
                EquipmentTypeId = null,
                ScoreWeight = 1,
                Envelope = "{}"
            };

            var res = await repo.IngestAsync(nullableEvent, CancellationToken.None);
            Assert.Equal(InteractionIngestStatus.Inserted, res.Status);

            db.ChangeTracker.Clear();
            var persisted = await db.InteractionEvents.FindAsync(msgId);
            Assert.NotNull(persisted);
            Assert.Null(persisted.CorrelationId);
            Assert.Null(persisted.UserId);
            Assert.Null(persisted.EquipmentListingId);
            Assert.Null(persisted.EquipmentTypeId);
        }
    }

    [Fact]
    public async Task Ingestion_CancelledToken_ThrowsOperationCanceledException()
    {
        var db = _fixture.CreateDbContext();
        await using (db)
        {
            await db.Database.MigrateAsync();
            var repo = new InteractionEventRepository(db);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var evt = new InteractionEvent
            {
                MessageId = Guid.NewGuid(),
                MessageType = "ListingViewed",
                Version = 1,
                Producer = "InventoryService",
                OccurredAt = DateTimeOffset.UtcNow,
                Envelope = "{}"
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await repo.IngestAsync(evt, cts.Token));
        }
    }
}
