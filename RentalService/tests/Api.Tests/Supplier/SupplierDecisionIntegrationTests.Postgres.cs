using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Services.Expiry;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.SupplierDecision;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Supplier;

public sealed partial class SupplierDecisionIntegrationTests
{
    [Fact]
    public async Task AcceptSubOrder_Postgres_ConcurrentReplay_CommitsConfirmationAndSingleEvent()
    {
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            await using var seed = new RentalServiceDbContext(options);
            await seed.Database.MigrateAsync();
            Guid supplierId = Guid.NewGuid();
            var (order, item) = CreatePaidOrderWithItem(supplierId);
            seed.RentalOrders.Add(order);
            await seed.SaveChangesAsync();
            int calls = 0;

            async Task AcceptAsync()
            {
                await using var db = new RentalServiceDbContext(options);
                var inventory = new StubInventoryClient
                {
                    ConfirmHandler = (group, _) =>
                    {
                        Assert.False(db.HasActiveTransaction);
                        Interlocked.Increment(ref calls);
                        return Task.FromResult(Confirmed(item, group));
                    }
                };
                var result = await CreateDecisionService(db, inventory)
                    .AcceptSubOrderAsync(item.RentalSubOrderId, "postgres-accept", CreateUserCaller(supplierId));
                Assert.True(result.IsSuccess, result.Error.Code);
                Assert.Equal("AwaitingHandover", result.Value.Status);
            }

            await Task.WhenAll(AcceptAsync(), AcceptAsync());
            Assert.Equal(1, calls);
            await using var verify = new RentalServiceDbContext(options);
            Assert.Equal(RentalItemStatus.AwaitingHandover, (await verify.RentalItems.SingleAsync()).Status);
            var receipt = await verify.RentalOperationReceipts.SingleAsync(r => r.Action == "SupplierAccept");
            Assert.Equal(RentalOperationReceipt.StatusSucceeded, receipt.Status);
            Assert.NotNull(receipt.ApiResponseJson);
            Assert.Single(await verify.RentalStateTransitions.Where(t => t.Action == "SupplierAccept").ToListAsync());
            Assert.Single(await verify.RentalOutboxMessages.Where(m => m.MessageType == "RentalItemConfirmed").ToListAsync());
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Theory]
    [InlineData("Enqueue", "Http")]
    [InlineData("Save", "Http")]
    [InlineData("Conflict", "Http")]
    [InlineData("Enqueue", "Scheduled")]
    [InlineData("Enqueue", "Reconcile")]
    [InlineData("Enqueue", "PayloadRecovery")]
    public async Task AcceptSubOrder_Postgres_PersistenceFailure_RollsBackAndRetriesSameProviderKey(string failure, string retry)
    {
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            await using var db = new RentalServiceDbContext(options);
            await db.Database.MigrateAsync();
            Guid supplierId = Guid.NewGuid();
            var (order, item) = CreatePaidOrderWithItem(supplierId);
            db.RentalOrders.Add(order);
            await db.SaveChangesAsync();
            List<string> providerKeys = [];
            bool injectFailure = true;
            var inventory = new StubInventoryClient
            {
                ConfirmHandler = async (group, request) =>
                {
                    Assert.False(db.HasActiveTransaction);
                    providerKeys.Add(request.OperationKey);
                    if (injectFailure && failure == "Conflict")
                    {
                        Guid id = OutboxMessageIdGenerator.ComputeDeterministicId(KafkaContractSpec.Producers.RentalService,
                            KafkaContractSpec.EventTypes.RentalItemConfirmed, item.Id.ToString("D"));
                        db.RentalOutboxMessages.Add(new RentalOutboxMessage(id, "conflicting-topic", order.Id.ToString("D"),
                            "ConflictingEvent", "{}", FixedNow));
                        await db.SaveChangesAsync();
                    }
                    else if (injectFailure)
                    {
                        // Fail only after the remote confirmation, leaving the durable intent intact.
                        await db.Database.ExecuteSqlRawAsync("""
                            CREATE FUNCTION rental.reject_confirmation() RETURNS trigger LANGUAGE plpgsql AS $$
                            BEGIN RAISE EXCEPTION 'Injected confirmation persistence failure'; END $$;
                            """);
                        string trigger = failure == "Enqueue"
                            ? "CREATE TRIGGER reject_confirmation BEFORE INSERT ON rental.rental_outbox_messages FOR EACH ROW EXECUTE FUNCTION rental.reject_confirmation()"
                            : "CREATE TRIGGER reject_confirmation BEFORE UPDATE ON rental.rental_orders FOR EACH ROW EXECUTE FUNCTION rental.reject_confirmation()";
                        await db.Database.ExecuteSqlRawAsync(trigger);
                    }
                    return Confirmed(item, group);
                }
            };
            var service = CreateDecisionService(db, inventory);
            if (failure == "Conflict")
            {
                var failed = await service.AcceptSubOrderAsync(item.RentalSubOrderId, "postgres-retry", CreateUserCaller(supplierId));
                Assert.True(failed.IsFailure);
                Assert.Equal("OUTBOX_MESSAGE_CONFLICT", failed.Error.Code);
            }
            else if (failure == "Enqueue")
            {
                var failed = await Assert.ThrowsAsync<PostgresException>(() =>
                    service.AcceptSubOrderAsync(item.RentalSubOrderId, "postgres-retry", CreateUserCaller(supplierId)));
                Assert.Equal("P0001", failed.SqlState);
            }
            else
            {
                await Assert.ThrowsAsync<DbUpdateException>(() =>
                    service.AcceptSubOrderAsync(item.RentalSubOrderId, "postgres-retry", CreateUserCaller(supplierId)));
            }

            Assert.False(db.HasActiveTransaction);
            Assert.Empty(db.ChangeTracker.Entries());
            await using (var verify = new RentalServiceDbContext(options))
            {
                Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, (await verify.RentalItems.SingleAsync()).Status);
                var receipt = await verify.RentalOperationReceipts.SingleAsync(r => r.Action == "SupplierAccept");
                Assert.Equal(RentalOperationReceipt.StatusProcessing, receipt.Status);
                Assert.False(receipt.IsSuccess);
                Assert.False(await verify.RentalStateTransitions.AnyAsync(t => t.Action == "SupplierAccept"));
                Assert.False(await verify.RentalOutboxMessages.AnyAsync(m => m.MessageType == "RentalItemConfirmed"));
            }

            if (failure == "Conflict")
            {
                await db.RentalOutboxMessages.Where(m => m.MessageType == "ConflictingEvent").ExecuteDeleteAsync();
            }
            else
            {
                string dropTrigger = failure == "Enqueue"
                    ? "DROP TRIGGER reject_confirmation ON rental.rental_outbox_messages"
                    : "DROP TRIGGER reject_confirmation ON rental.rental_orders";
                await db.Database.ExecuteSqlRawAsync(dropTrigger);
            }
            injectFailure = false;
            if (retry == "Http")
            {
                var retried = await service.AcceptSubOrderAsync(item.RentalSubOrderId, "postgres-retry", CreateUserCaller(supplierId));
                Assert.True(retried.IsSuccess, retried.Error.Code);
                Assert.Equal("AwaitingHandover", retried.Value.Status);
            }
            else
            {
                if (retry == "PayloadRecovery")
                {
                    await db.RentalOperationReceipts.Where(r => r.Action == "SupplierAccept")
                        .ExecuteUpdateAsync(set => set.SetProperty(r => r.InventoryOperationKey, (string?)null));
                }
                var receipts = new OperationReceiptRepository(db);
                var pending = Assert.Single(await receipts.GetPendingOperationDescriptorsAsync(FixedNow, 10));
                var executor = new RentalOperationExecutor(new RentalOrderRepository(db), receipts, new OutboxRepository(db),
                    new FakeOrderLockManager(), inventory, new StubPaymentClient(), db, new ConfigurationBuilder().Build(),
                    NullLogger<RentalOperationExecutor>.Instance, new TestTimeProvider(FixedNow));
                var recovered = retry == "Reconcile"
                    ? await executor.ExecuteReconcileAcceptAsync(order.Id, item.Id, pending.OperationId)
                    : await executor.ExecuteScheduledOperationAsync(pending);
                Assert.True(recovered.IsSuccess, recovered.Error.Code);
                Assert.Equal(RentalItemStatus.AwaitingHandover, (await db.RentalItems.SingleAsync()).Status);
                Assert.Equal(RentalOperationReceipt.StatusSucceeded,
                    (await db.RentalOperationReceipts.SingleAsync(r => r.Action == "SupplierAccept")).Status);
            }
            Assert.Equal(2, providerKeys.Count);
            Assert.Equal(providerKeys[0], providerKeys[1]);
            Assert.Single(await db.RentalOutboxMessages.Where(m => m.MessageType == "RentalItemConfirmed").ToListAsync());
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Concurrent_AcceptSubOrder_And_SupplierTimeout_ExactlyOneWins()
    {
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            await using var seed = new RentalServiceDbContext(options);
            await seed.Database.MigrateAsync();
            Guid supplierId = Guid.NewGuid();
            DateTimeOffset deadline = FixedNow.AddSeconds(10);
            var (order, item) = CreatePaidOrderWithItem(supplierId, deadline: deadline);
            seed.RentalOrders.Add(order);
            await seed.SaveChangesAsync();

            Result<SubOrderDecisionResponse>? acceptResult = null;
            Result? expiryResult = null;

            async Task RunAcceptAsync()
            {
                await using var db = new RentalServiceDbContext(options);
                var inventory = new StubInventoryClient
                {
                    ConfirmHandler = (group, _) => Task.FromResult(Confirmed(item, group))
                };
                var service = CreateDecisionService(db, inventory, now: FixedNow);
                acceptResult = await service.AcceptSubOrderAsync(item.RentalSubOrderId, "postgres-accept-timeout-race", CreateUserCaller(supplierId));
            }

            async Task RunExpiryAsync()
            {
                await using var db = new RentalServiceDbContext(options);
                var inventory = new StubInventoryClient();
                var expiry = CreateExpiryService(db, inventory, now: deadline.AddMinutes(1));
                expiryResult = await expiry.ExpireSingleItemSupplierTimeoutAsync(item.Id);
            }

            await Task.WhenAll(RunAcceptAsync(), RunExpiryAsync());

            await using var verify = new RentalServiceDbContext(options);
            var finalItem = await verify.RentalItems.SingleAsync();
            var outbox = await verify.RentalOutboxMessages.ToListAsync();

            bool acceptWon = finalItem.Status == RentalItemStatus.AwaitingHandover;
            bool timeoutWon = finalItem.Status == RentalItemStatus.Expired;

            Assert.True(acceptWon ^ timeoutWon, $"Expected exactly one winner (AwaitingHandover or Expired), but got {finalItem.Status}");

            if (acceptWon)
            {
                Assert.NotNull(acceptResult);
                Assert.True(acceptResult.IsSuccess, acceptResult.Error.Code);
                Assert.Single(outbox, m => m.MessageType == "RentalItemConfirmed");
                Assert.DoesNotContain(outbox, m => m.MessageType == "RentalItemExpired");
            }
            else
            {
                Assert.NotNull(acceptResult);
                Assert.True(acceptResult.IsFailure);
                Assert.DoesNotContain(outbox, m => m.MessageType == "RentalItemConfirmed");
                Assert.Single(outbox, m => m.MessageType == "RentalItemExpired");
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Concurrent_AcceptSubOrder_And_RenterCancel_ExactlyOneWins()
    {
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            await using var seed = new RentalServiceDbContext(options);
            await seed.Database.MigrateAsync();
            Guid supplierId = Guid.NewGuid();
            DateTimeOffset deadline = FixedNow.AddHours(24);
            var (order, item) = CreatePaidOrderWithItem(supplierId, deadline: deadline);
            seed.RentalOrders.Add(order);
            await seed.SaveChangesAsync();

            Result<SubOrderDecisionResponse>? acceptResult = null;
            Result<CancelOrderResponse>? cancelResult = null;

            async Task RunAcceptAsync()
            {
                await using var db = new RentalServiceDbContext(options);
                var inventory = new StubInventoryClient
                {
                    ConfirmHandler = (group, _) => Task.FromResult(Confirmed(item, group))
                };
                var service = CreateDecisionService(db, inventory, now: FixedNow);
                acceptResult = await service.AcceptSubOrderAsync(item.RentalSubOrderId, "postgres-accept-cancel-race", CreateUserCaller(supplierId));
            }

            async Task RunCancelAsync()
            {
                await using var db = new RentalServiceDbContext(options);
                var executor = CreateExecutor(db, new StubInventoryClient(), now: FixedNow);
                cancelResult = await executor.ExecuteRenterCancelOrderAsync(order.Id, order.RenterId, "Customer decided not to rent", "postgres-renter-cancel-race");
            }

            await Task.WhenAll(RunAcceptAsync(), RunCancelAsync());

            await using var verify = new RentalServiceDbContext(options);
            var finalItem = await verify.RentalItems.SingleAsync();
            var outbox = await verify.RentalOutboxMessages.ToListAsync();

            bool acceptWon = finalItem.Status == RentalItemStatus.AwaitingHandover;
            bool cancelWon = finalItem.Status == RentalItemStatus.Cancelled;

            Assert.True(acceptWon ^ cancelWon, $"Expected exactly one winner (AwaitingHandover or Cancelled), but got {finalItem.Status}");

            if (acceptWon)
            {
                Assert.NotNull(acceptResult);
                Assert.True(acceptResult.IsSuccess, acceptResult.Error.Code);
                Assert.NotNull(cancelResult);
                Assert.True(cancelResult.IsFailure);
                Assert.Single(outbox, m => m.MessageType == "RentalItemConfirmed");
                Assert.DoesNotContain(outbox, m => m.MessageType == "RentalItemCancelled");
            }
            else
            {
                Assert.NotNull(acceptResult);
                Assert.True(acceptResult.IsFailure);
                Assert.NotNull(cancelResult);
                Assert.True(cancelResult.IsSuccess, cancelResult.Error.Code);
                Assert.DoesNotContain(outbox, m => m.MessageType == "RentalItemConfirmed");
                Assert.Single(outbox, m => m.MessageType == "RentalItemCancelled");
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    private static ExpiryWorkflowService CreateExpiryService(
        RentalServiceDbContext db,
        IInventoryRentalClient inventoryClient,
        IPaymentRentalClient? paymentClient = null,
        DateTimeOffset? now = null)
    {
        var orderRepo = new RentalOrderRepository(db);
        var receiptRepo = new OperationReceiptRepository(db);
        var outboxRepo = new OutboxRepository(db);
        var lockManager = new FakeOrderLockManager();
        var time = new TestTimeProvider(now ?? FixedNow);
        var config = new ConfigurationBuilder().Build();
        var executor = new RentalOperationExecutor(orderRepo, receiptRepo, outboxRepo, lockManager, inventoryClient, paymentClient ?? new StubPaymentClient(), db, config, NullLogger<RentalOperationExecutor>.Instance, time);
        return new ExpiryWorkflowService(orderRepo, outboxRepo, lockManager, executor, db, config, NullLogger<ExpiryWorkflowService>.Instance, time);
    }

    private static RentalOperationExecutor CreateExecutor(
        RentalServiceDbContext db,
        IInventoryRentalClient inventoryClient,
        IPaymentRentalClient? paymentClient = null,
        DateTimeOffset? now = null)
    {
        var orderRepo = new RentalOrderRepository(db);
        var receiptRepo = new OperationReceiptRepository(db);
        var outboxRepo = new OutboxRepository(db);
        var lockManager = new FakeOrderLockManager();
        var time = new TestTimeProvider(now ?? FixedNow);
        var config = new ConfigurationBuilder().Build();
        return new RentalOperationExecutor(orderRepo, receiptRepo, outboxRepo, lockManager, inventoryClient, paymentClient ?? new StubPaymentClient(), db, config, NullLogger<RentalOperationExecutor>.Instance, time);
    }

    private static Result<ConfirmAllocationResponse> Confirmed(RentalItem item, string group) =>
        Result.Success(new ConfirmAllocationResponse("REC-CONF", group, FixedNow, false,
            item.UnitAssignments.Select(u => new ConfirmedAllocationUnitDto(u.AllocationId, u.AssetId, "Confirmed")).ToList()));
}
