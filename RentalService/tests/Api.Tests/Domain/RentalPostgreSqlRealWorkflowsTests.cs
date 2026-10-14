using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Domain;

public sealed class RentalPostgreSqlRealWorkflowsTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 8, 8, 15, 30, TimeSpan.Zero);

    private static string BaseConnectionString =>
        Dprental.RentalService.Api.Tests.TestHelpers.PostgresTestDatabaseHelper.BaseConnectionString;

    private static async Task<bool> IsPostgreSqlAvailableAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(BaseConnectionString);
            await conn.OpenAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> CreateTestDatabaseAsync()
    {
        string dbName = $"rental_rw_{Guid.NewGuid():N}";
        var databaseConnection = new NpgsqlConnectionStringBuilder(BaseConnectionString)
        {
            Database = dbName
        };
        await using var conn = new NpgsqlConnection(BaseConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{dbName}\";";
        await cmd.ExecuteNonQueryAsync();

        return databaseConnection.ConnectionString;
    }

    private static async Task DropTestDatabaseAsync(string connString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connString);
            string dbName = builder.Database!;

            await using var conn = new NpgsqlConnection(BaseConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE);";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private sealed class StubInventoryClient : IInventoryRentalClient
    {
        public Task<Result<ConfirmAllocationResponse>> ConfirmAllocationAsync(string allocationGroupId, ConfirmAllocationRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new ConfirmAllocationResponse("REC-CONF", allocationGroupId, FixedNow, false, [])));

        public Task<Result<ExtendHoldResponse>> ExtendHoldAsync(string allocationGroupId, ExtendHoldRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new ExtendHoldResponse("REC-HOLD", allocationGroupId, request.NewHoldExpiresAt, FixedNow, false)));

        public Task<Result<ReleaseAllocationResponse>> ReleaseAllocationAsync(string allocationGroupId, ReleaseAllocationRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new ReleaseAllocationResponse("REC-REL", request.OperationKey, allocationGroupId, FixedNow, false)));

        public Task<Result<ListingRentalSnapshotDto>> GetListingRentalSnapshotAsync(Guid listingId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<ListingAvailabilityCheckResponse>> CheckListingAvailabilityAsync(ListingAvailabilityCheckRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CreateHoldResponse>> CreateHoldAsync(CreateHoldRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CancelAllocationResponse>> CancelAllocationAsync(string allocationGroupId, CancelAllocationRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<MarkAssetsOverdueResponse>> MarkAssetsOverdueAsync(string allocationGroupId, MarkAssetsOverdueRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<MarkAssetsReturnedResponse>> MarkAssetsReturnedAsync(string allocationGroupId, MarkAssetsReturnedRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<BlockDamagedAssetsResponse>> BlockDamagedAssetsAsync(string allocationGroupId, BlockDamagedAssetsRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class StubPaymentClient : IPaymentRentalClient
    {
        public Task<Result<RequestRefundResponse>> RequestRefundAsync(RequestRefundRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new RequestRefundResponse("REF-001", request.OperationKey, "Accepted", request.TotalRefundAmount, "VND", false)));

        public Task<Result<CreateInvoiceResponse>> CreateInvoiceAsync(CreateInvoiceRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<CancelInvoiceResponse>> CancelInvoiceAsync(CancelInvoiceRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Result<SettleRentalResponse>> SettleRentalAsync(SettleRentalRequest request, CancellationToken ct = default) => throw new NotImplementedException();
    }

    [Fact]
    public async Task PostgreSql_MigrateAsync_AppliesAllMigrations_AndVerifiesSchema()
    {
        if (!await IsPostgreSqlAvailableAsync()) return;

        string connString = await CreateTestDatabaseAsync();
        try
        {
            var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
                .UseNpgsql(connString)
                .Options;

            await using (var context = new RentalServiceDbContext(options))
            {
                // Run MigrateAsync to test all migrations
                await context.Database.MigrateAsync();

                // Verify tables exist in PostgreSQL
                await using var conn = new NpgsqlConnection(connString);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = 'rental';";
                var tables = new List<string>();
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    tables.Add(reader.GetString(0));
                }

                Assert.Contains("rental_orders", tables);
                Assert.Contains("rental_items", tables);
                Assert.Contains("rental_unit_assignments", tables);
                Assert.Contains("rental_operation_receipts", tables);
                Assert.Contains("rental_inbox_messages", tables);
                Assert.Contains("rental_outbox_messages", tables);
                Assert.Contains("rental_sub_orders", tables);
                Assert.Equal(15, tables.Count);
            }
        }
        finally
        {
            await DropTestDatabaseAsync(connString);
        }
    }

    [Fact]
    public async Task PostgreSql_MigrationDataBackfill_UpdatesOldStatusAndReceipts()
    {
        if (!await IsPostgreSqlAvailableAsync()) return;

        string connString = await CreateTestDatabaseAsync();
        try
        {
            var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
                .UseNpgsql(connString)
                .Options;

            // 1. Migrate to InitialRentalModel
            await using (var context = new RentalServiceDbContext(options))
            {
                var migrator = context.Database.GetService<IMigrator>();
                await migrator.MigrateAsync("20261008091237_InitialRentalModel");

                // 2. Insert order with legacy Status = 1 and receipts
                await using var conn = new NpgsqlConnection(connString);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();

                Guid orderId = Guid.NewGuid();
                Guid renterId = Guid.NewGuid();
                Guid receipt1 = Guid.NewGuid();
                Guid receipt2 = Guid.NewGuid();

                cmd.CommandText = $"""
                    INSERT INTO rental.rental_orders ("Id", "OrderCode", "RenterId", "Status", "Currency", "DeliveryMethod", "RecipientName", "RecipientPhone", "DeliveryAddress", "TermsVersion", "TermsDocumentRef", "TermsContentHash", "PaymentDeadline", "CheckoutOperationId", "CreatedAt", "UpdatedAt", "Version", "RefundDestinationRef", "LatePaymentRefundRequired")
                    VALUES ('{orderId}', 'ORD-LEGACY-001', '{renterId}', 1, 'VND', 'Pickup', 'Cust', '0909', 'Addr', 'v1', 'doc', 'hash', NOW(), 'op-leg', NOW(), NOW(), 1, 'ORIGINAL', false);

                    INSERT INTO rental.rental_operation_receipts ("Id", "RentalOrderId", "OperationId", "Action", "Fingerprint", "IsSuccess", "CreatedAt")
                    VALUES ('{receipt1}', '{orderId}', 'op-rec-1', 'ActionA', 'hashA', true, NOW()),
                           ('{receipt2}', '{orderId}', 'op-rec-2', 'ActionB', 'hashB', false, NOW());
                """;
                await cmd.ExecuteNonQueryAsync();

                // 3. Migrate forward to latest migration
                await migrator.MigrateAsync();

                // 4. Verify backfill
                cmd.CommandText = $"SELECT \"Status\" FROM rental.rental_orders WHERE \"Id\" = '{orderId}';";
                var orderStatus = Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
                Assert.Equal(9, orderStatus);

                cmd.CommandText = $"SELECT \"Status\" FROM rental.rental_operation_receipts WHERE \"Id\" = '{receipt1}';";
                var receipt1Status = (string)(await cmd.ExecuteScalarAsync())!;
                Assert.Equal("Succeeded", receipt1Status);

                cmd.CommandText = $"SELECT \"Status\" FROM rental.rental_operation_receipts WHERE \"Id\" = '{receipt2}';";
                var receipt2Status = (string)(await cmd.ExecuteScalarAsync())!;
                Assert.Equal("Failed", receipt2Status);

                // 5. Test rollback to InitialRentalModel
                await migrator.MigrateAsync("20261008091237_InitialRentalModel");

                cmd.CommandText = $"SELECT \"Status\" FROM rental.rental_orders WHERE \"Id\" = '{orderId}';";
                var rolledBackStatus = Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
                Assert.Equal(1, rolledBackStatus);
            }
        }
        finally
        {
            await DropTestDatabaseAsync(connString);
        }
    }

    [Fact]
    public async Task PostgreSql_AdvisoryLock_SerializesAcrossSeparateConnections_AndFailsClosed()
    {
        if (!await IsPostgreSqlAvailableAsync()) return;

        string connString = await CreateTestDatabaseAsync();
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("ConnectionStrings:Postgres", connString) })
                .Build();

            var lockManager1 = new PostgresOrderLockManager(config, NullLogger<PostgresOrderLockManager>.Instance);
            var lockManager2 = new PostgresOrderLockManager(config, NullLogger<PostgresOrderLockManager>.Instance);
            Guid orderId = Guid.NewGuid();

            // Lock 1 acquires lock on orderId
            await using var lock1 = await lockManager1.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(2));

            // Lock 2 attempts to acquire lock on same orderId -> must time out and fail closed
            await Assert.ThrowsAsync<TimeoutException>(async () =>
            {
                await lockManager2.AcquireOrderLockAsync(orderId, TimeSpan.FromMilliseconds(300));
            });

            // Dispose Lock 1
            await lock1.DisposeAsync();

            // Now Lock 2 can acquire successfully
            await using var lock2 = await lockManager2.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(2));
            Assert.NotNull(lock2);
        }
        finally
        {
            await DropTestDatabaseAsync(connString);
        }
    }

    [Fact]
    public async Task PostgreSql_OptimisticConcurrency_ThrowsDbUpdateConcurrencyException_OnConcurrentUpdate()
    {
        if (!await IsPostgreSqlAvailableAsync()) return;

        string connString = await CreateTestDatabaseAsync();
        try
        {
            var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
                .UseNpgsql(connString)
                .Options;

            Guid orderId = Guid.NewGuid();
            var item = new InitialRentalItemDto(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
                new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18),
                "Sony A7", null, "Pickup", 200_000m, 500_000m, "grp-01",
                [new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
            TestListingSnapshots.Default);

            var order = RentalOrder.Create(orderId, "ORD-CONC-001", Guid.NewGuid(), "VND", "Pickup", "Cust", "0909", "Addr", null, "v1", "doc", "h", "op-c", FixedNow, [item]).Value;

            // 1. Seed order (Version = 1)
            await using (var ctx = new RentalServiceDbContext(options))
            {
                await ctx.Database.EnsureCreatedAsync();
                ctx.RentalOrders.Add(order);
                await ctx.SaveChangesAsync();
            }

            // 2. Load in Context A and Context B
            await using var ctxA = new RentalServiceDbContext(options);
            await using var ctxB = new RentalServiceDbContext(options);

            var orderA = await ctxA.RentalOrders.FirstAsync(o => o.Id == orderId);
            var orderB = await ctxB.RentalOrders.FirstAsync(o => o.Id == orderId);

            // Context A updates and saves (Version becomes 2)
            orderA.MarkAsPaid("INV-1", "PAY-1", FixedNow, FixedNow);
            orderA.IncrementVersion();
            await ctxA.SaveChangesAsync();

            // Context B tries to update with stale Version (Version 1) -> must throw DbUpdateConcurrencyException
            orderB.MarkPaymentExpired(FixedNow, "op-stale");
            orderB.IncrementVersion();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => ctxB.SaveChangesAsync());
        }
        finally
        {
            await DropTestDatabaseAsync(connString);
        }
    }

    [Fact]
    public async Task PostgreSql_OutboxLeaseClaiming_AtomicallyLocksAndClaimsBatch()
    {
        if (!await IsPostgreSqlAvailableAsync()) return;

        string connString = await CreateTestDatabaseAsync();
        try
        {
            var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
                .UseNpgsql(connString)
                .Options;

            await using (var ctx = new RentalServiceDbContext(options))
            {
                await ctx.Database.EnsureCreatedAsync();
                var outboxRepo = new OutboxRepository(ctx);

                // Add 3 pending messages
                for (int i = 0; i < 3; i++)
                {
                    var msg = new RentalOutboxMessage(Guid.NewGuid(), "rental.events.v1", "key", "TestEvent", "{}", FixedNow);
                    await outboxRepo.AddAsync(msg);
                }
                await ctx.SaveChangesAsync();
            }

            await using (var ctx = new RentalServiceDbContext(options))
            {
                var outboxRepo = new OutboxRepository(ctx);
                string worker1 = "worker-1";

                // Worker 1 claims batch with 10s lease
                var claimed = await outboxRepo.ClaimPendingBatchAsync(worker1, TimeSpan.FromSeconds(10), 50);
                Assert.Equal(3, claimed.Count);
                Assert.All(claimed, m => Assert.Equal(worker1, m.LockedBy));

                // Worker 2 attempts to claim -> must return 0 because all 3 are locked!
                string worker2 = "worker-2";
                var claimedByWorker2 = await outboxRepo.ClaimPendingBatchAsync(worker2, TimeSpan.FromSeconds(10), 50);
                Assert.Empty(claimedByWorker2);
            }
        }
        finally
        {
            await DropTestDatabaseAsync(connString);
        }
    }

    [Fact]
    public async Task PostgreSql_AtomicMultiEntityRollback_OnFailure()
    {
        if (!await IsPostgreSqlAvailableAsync()) return;

        string connString = await CreateTestDatabaseAsync();
        try
        {
            var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
                .UseNpgsql(connString)
                .Options;

            Guid orderId = Guid.NewGuid();
            var item = new InitialRentalItemDto(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
                new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18),
                "Sony A7", null, "Pickup", 200_000m, 500_000m, "grp-01",
                [new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
            TestListingSnapshots.Default);

            var order = RentalOrder.Create(orderId, "ORD-ATOMIC-001", Guid.NewGuid(), "VND", "Pickup", "Cust", "0909", "Addr", null, "v1", "doc", "h", "op-c", FixedNow, [item]).Value;

            Guid outboxId = Guid.NewGuid();
            var outboxMsg = new RentalOutboxMessage(outboxId, "rental.events.v1", orderId.ToString("D"), "TestEvent", "{}", FixedNow);

            var receipt1 = new RentalOperationReceipt(Guid.NewGuid(), orderId, "op-dup", "Action1", "hash1", true, null, null, FixedNow);
            var receipt2 = new RentalOperationReceipt(Guid.NewGuid(), orderId, "op-dup", "Action2", "hash2", true, null, null, FixedNow);

            await using (var ctx = new RentalServiceDbContext(options))
            {
                await ctx.Database.EnsureCreatedAsync();

                await using var tx = await ctx.Database.BeginTransactionAsync();
                try
                {
                    ctx.RentalOrders.Add(order);
                    ctx.RentalOutboxMessages.Add(outboxMsg);
                    ctx.RentalOperationReceipts.Add(receipt1);
                    ctx.RentalOperationReceipts.Add(receipt2);

                    await ctx.SaveChangesAsync();
                    await tx.CommitAsync();
                    Assert.Fail("Should have thrown DbUpdateException due to duplicate key.");
                }
                catch (DbUpdateException)
                {
                    await tx.RollbackAsync();
                }
            }

            await using (var verifyCtx = new RentalServiceDbContext(options))
            {
                var persistedOrder = await verifyCtx.RentalOrders.FirstOrDefaultAsync(o => o.Id == orderId);
                var persistedOutbox = await verifyCtx.RentalOutboxMessages.FirstOrDefaultAsync(m => m.Id == outboxId);

                Assert.Null(persistedOrder);
                Assert.Null(persistedOutbox);
            }
        }
        finally
        {
            await DropTestDatabaseAsync(connString);
        }
    }
}
