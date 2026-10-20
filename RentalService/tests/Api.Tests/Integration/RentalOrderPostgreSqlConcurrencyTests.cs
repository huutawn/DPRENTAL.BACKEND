using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Integration;

public sealed class RentalOrderPostgreSqlConcurrencyTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 19, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PostgreSql_ConcurrentWriters_OnDifferentItemsWithUnchangedOrderStatus_ThrowsConcurrencyException_AndRollsBackCleanly()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL test database is required for concurrency test, but it is not available.");
        }

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);

            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid supplierId = Guid.NewGuid();
            Guid item1Id = Guid.NewGuid();
            Guid item2Id = Guid.NewGuid();

            var assignments1 = new List<InitialRentalUnitAssignmentDto>
            {
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
            };
            var assignments2 = new List<InitialRentalUnitAssignmentDto>
            {
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
            };

            var item1Dto = new InitialRentalItemDto(
                item1Id,
                Guid.NewGuid(),
                supplierId,
                1,
                new DateOnly(2026, 10, 20),
                new DateOnly(2026, 10, 25),
                "Equipment 1",
                "Description 1",
                "Kho HCM",
                100_000m,
                500_000m,
                $"group-{item1Id:N}",
                assignments1,
                TestListingSnapshots.Default);

            var item2Dto = new InitialRentalItemDto(
                item2Id,
                Guid.NewGuid(),
                supplierId,
                1,
                new DateOnly(2026, 10, 20),
                new DateOnly(2026, 10, 25),
                "Equipment 2",
                "Description 2",
                "Kho HCM",
                200_000m,
                600_000m,
                $"group-{item2Id:N}",
                assignments2,
                TestListingSnapshots.Default);

            var order = RentalOrder.Create(
                orderId,
                "ORD-CONC-001",
                renterId,
                "VND",
                "Pickup",
                "Nguyen Van An",
                "0901234567",
                "123 Duong 1",
                null,
                "v1.0",
                "terms-doc",
                "terms-hash",
                "op-init",
                FixedNow,
                [item1Dto, item2Dto]).Value;

            order.MarkAsPaid("INV-CONC-001", "PAY-CONC-001", FixedNow, FixedNow);

            // 1. Seed Order with Version = 1 and status = Paid
            await using (var seedCtx = new RentalServiceDbContext(options))
            {
                await seedCtx.Database.EnsureCreatedAsync();
                seedCtx.RentalOrders.Add(order);
                await seedCtx.SaveChangesAsync();
            }

            // 2. Load the same Order in two concurrent contexts
            await using var ctxA = new RentalServiceDbContext(options);
            await using var ctxB = new RentalServiceDbContext(options);

            var orderA = await ctxA.RentalOrders
                .Include(o => o.Items)
                .Include(o => o.StateTransitions)
                .FirstAsync(o => o.Id == orderId);

            var orderB = await ctxB.RentalOrders
                .Include(o => o.Items)
                .Include(o => o.StateTransitions)
                .FirstAsync(o => o.Id == orderId);

            Assert.Equal(1, orderA.Version);
            Assert.Equal(1, orderB.Version);
            Assert.Equal(RentalOrderStatus.Paid, orderA.Status);
            Assert.Equal(RentalOrderStatus.Paid, orderB.Status);

            // Writer A: Mutates Item 1 (Accept item 1).
            // Item 2 is still PendingSupplierConfirmation, so Order.Status stays Paid!
            var itemA1 = orderA.Items.First(i => i.Id == item1Id);
            itemA1.ApplySupplierAccept(supplierId, FixedNow.AddMinutes(5), "CONF-INV-1", "op-writer-A");
            bool rollupA = RentalOrderStatusPolicy.Apply(orderA, FixedNow.AddMinutes(5), "op-writer-A");
            Assert.False(rollupA); // Status does not change: remains Paid
            Assert.Equal(RentalOrderStatus.Paid, orderA.Status);

            // Application records read model update because Item 1 was mutated
            orderA.RecordReadModelUpdate(FixedNow.AddMinutes(5));
            Assert.Equal(2, orderA.Version);

            ctxA.RentalOutboxMessages.Add(new RentalOutboxMessage(
                Guid.NewGuid(), "rental.events.v1", orderId.ToString("D"), "RentalItemConfirmed", "{}", FixedNow.AddMinutes(5)));

            // Writer A commits successfully
            await ctxA.SaveChangesAsync();

            // Writer B: Mutates Item 2 (Reject item 2 on stale context)
            var itemB2 = orderB.Items.First(i => i.Id == item2Id);
            itemB2.ApplySupplierReject(supplierId, FixedNow.AddMinutes(10), "Out of stock", null, "op-writer-B");
            RentalOrderStatusPolicy.Apply(orderB, FixedNow.AddMinutes(10), "op-writer-B");
            orderB.RecordReadModelUpdate(FixedNow.AddMinutes(10)); // Attempts to bump from stale Version 1 to 2

            var failedOutbox = new RentalOutboxMessage(
                Guid.NewGuid(), "rental.events.v1", orderId.ToString("D"), "RentalItemRejected", "{}", FixedNow.AddMinutes(10));
            ctxB.RentalOutboxMessages.Add(failedOutbox);

            // Writer B attempts to commit in a transaction -> must throw DbUpdateConcurrencyException
            await using var txB = await ctxB.Database.BeginTransactionAsync();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => ctxB.SaveChangesAsync());
            await txB.RollbackAsync();

            // 3. Verify that rollback leaves NO trace of Writer B (item, transition, or outbox)
            await using (var verifyCtx = new RentalServiceDbContext(options))
            {
                var reloadedOrder = await verifyCtx.RentalOrders
                    .Include(o => o.Items)
                    .Include(o => o.StateTransitions)
                    .FirstAsync(o => o.Id == orderId);

                // Version is 2 (from Writer A only)
                Assert.Equal(2, reloadedOrder.Version);
                Assert.Equal(RentalOrderStatus.Paid, reloadedOrder.Status);

                // Item 1 reflects Writer A's mutation
                var reloadedItem1 = reloadedOrder.Items.First(i => i.Id == item1Id);
                Assert.Equal(RentalItemStatus.AwaitingHandover, reloadedItem1.Status);
                Assert.Equal("CONF-INV-1", reloadedItem1.InventoryConfirmationRef);

                // Item 2 was NOT mutated by Writer B (remains PendingSupplierConfirmation)
                var reloadedItem2 = reloadedOrder.Items.First(i => i.Id == item2Id);
                Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, reloadedItem2.Status);

                // Transitions contain Writer A's transition, but NOT Writer B's
                Assert.Contains(reloadedOrder.StateTransitions, t => t.OperationId == "op-writer-A");
                Assert.DoesNotContain(reloadedOrder.StateTransitions, t => t.OperationId == "op-writer-B");

                // Outbox contains Writer A's message, but NOT Writer B's
                var outboxMessages = await verifyCtx.RentalOutboxMessages.ToListAsync();
                Assert.Contains(outboxMessages, m => m.MessageType == "RentalItemConfirmed");
                Assert.DoesNotContain(outboxMessages, m => m.Id == failedOutbox.Id);

                // 4. Replay after reload: replaying Writer A does not duplicate transitions or increment Version
                int transitionCountBeforeReplay = reloadedOrder.StateTransitions.Count;
                long versionBeforeReplay = reloadedOrder.Version;
                DateTimeOffset updatedBeforeReplay = reloadedOrder.UpdatedAt;

                bool replayRollup = RentalOrderStatusPolicy.Apply(reloadedOrder, FixedNow.AddMinutes(15), "op-writer-A");
                Assert.False(replayRollup);
                Assert.Equal(versionBeforeReplay, reloadedOrder.Version);
                Assert.Equal(transitionCountBeforeReplay, reloadedOrder.StateTransitions.Count);
                Assert.Equal(updatedBeforeReplay, reloadedOrder.UpdatedAt);
            }
        }
        finally
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }
}
