using System.Data;
using System.Data.Common;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using Xunit;
using Xunit.Abstractions;

namespace Dprental.RentalService.Api.Tests.Domain;

public sealed class SupplierConfirmationStatisticsQueryAndMigrationPostgreSqlTests
{
    private readonly ITestOutputHelper _output;

    public SupplierConfirmationStatisticsQueryAndMigrationPostgreSqlTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static async Task EnsurePostgreSqlAvailableAsync()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available at test connection string.");
        }
    }

    public sealed record CapturedCommandInfo(
        string CommandText,
        IReadOnlyList<(string Name, object? Value, DbType DbType)> Parameters);

    private sealed class TestQueryCommandInterceptor : DbCommandInterceptor
    {
        public List<CapturedCommandInfo> ExecutedCommands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Capture(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Capture(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Capture(DbCommand command)
        {
            var list = new List<(string Name, object? Value, DbType DbType)>();
            foreach (DbParameter p in command.Parameters)
            {
                list.Add((p.ParameterName, p.Value, p.DbType));
            }
            ExecutedCommands.Add(new CapturedCommandInfo(command.CommandText, list));
        }
    }

    [Fact]
    public async Task Migration_UpCreatesIndex_DownDropsOnlyNewIndex()
    {
        await EnsurePostgreSqlAvailableAsync();

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using var ctx = new RentalServiceDbContext(options);

            // 1. Run all migrations up to latest (includes SupplierConfirmationStatisticsIndex)
            await ctx.Database.MigrateAsync();

            // 2. Verify new composite index exists
            await using var conn = new NpgsqlConnection(connString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM pg_indexes WHERE schemaname = 'rental' AND tablename = 'rental_state_transitions' AND indexname = 'IX_rental_state_transitions_RentalItemId_Action_OccurredAt';";
            long indexCount = (long)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal(1, indexCount);

            // 3. Roll back to prior migration (PaymentAndSupplierConfirmationMigration)
            var migrator = ctx.GetService<IMigrator>();
            await migrator.MigrateAsync("PaymentAndSupplierConfirmationMigration");

            // 4. Verify new composite index is dropped
            long indexCountAfterDown = (long)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal(0, indexCountAfterDown);

            // Verify base table still exists
            cmd.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'rental' AND table_name = 'rental_state_transitions';";
            long tableCount = (long)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal(1, tableCount);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task GetSupplierConfirmationStatisticsAsync_LargeScaleFixture_ExecutesSingleCapturedCommandAndExplainAnalyze()
    {
        await EnsurePostgreSqlAvailableAsync();

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var interceptor = new TestQueryCommandInterceptor();
            var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
                .UseNpgsql(connString)
                .AddInterceptors(interceptor)
                .Options;

            await using (var setupCtx = new RentalServiceDbContext(options))
            {
                await setupCtx.Database.EnsureCreatedAsync();
            }

            Guid targetSupplierId = Guid.NewGuid();
            Guid otherSupplier1 = Guid.NewGuid();
            Guid otherSupplier2 = Guid.NewGuid();

            var fromUtc = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
            var toUtc = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

            // Seed 10,000 items (1,000 target supplier + 9,000 other suppliers) using PostgreSQL Binary COPY for high performance
            await SeedLargeScaleFixtureAsync(connString, targetSupplierId, otherSupplier1, otherSupplier2, fromUtc, toUtc);

            await using var queryCtx = new RentalServiceDbContext(options);
            var repo = new RentalOrderRepository(queryCtx);

            // 1. Call real repository method
            var stats = await repo.GetSupplierConfirmationStatisticsAsync(targetSupplierId, fromUtc, toUtc);

            // 2. Assert counts match expected fixture values
            Assert.Equal(300, stats.AcceptedCount);
            Assert.Equal(150, stats.RejectedCount);
            Assert.Equal(80, stats.ConfirmationTimeoutCount);
            Assert.Equal(60, stats.CancelAfterAcceptCount);

            // 3. Assert exactly one reader command was executed by repository
            Assert.Single(interceptor.ExecutedCommands);
            var capturedCommand = interceptor.ExecutedCommands[0];

            // 4. Assert parameters contain supplier and UTC boundaries without string concatenation into SQL
            Assert.Contains(capturedCommand.Parameters, p => p.Value is Guid g && g == targetSupplierId);
            Assert.Contains(capturedCommand.Parameters, p => p.Value is DateTimeOffset d && d == fromUtc);
            Assert.Contains(capturedCommand.Parameters, p => p.Value is DateTimeOffset d && d == toUtc);
            Assert.DoesNotContain(targetSupplierId.ToString(), capturedCommand.CommandText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("2026-10-01", capturedCommand.CommandText, StringComparison.OrdinalIgnoreCase);

            // 5. Assert change tracker is completely empty (no in-memory entity materialization)
            Assert.Empty(queryCtx.ChangeTracker.Entries());

            // 6. Run EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) on the captured production command
            await using var conn = new NpgsqlConnection(connString);
            await conn.OpenAsync();
            await using var explainCmd = conn.CreateCommand();
            explainCmd.CommandText = $"EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) {capturedCommand.CommandText}";
            foreach (var (name, value, _) in capturedCommand.Parameters)
            {
                explainCmd.Parameters.Add(new NpgsqlParameter(name, value));
            }

            string jsonPlan = (string)(await explainCmd.ExecuteScalarAsync())!;
            _output.WriteLine($"Captured Production Command Execution Plan:\n{jsonPlan}");

            Assert.NotEmpty(jsonPlan);
            Assert.Contains("Aggregate", jsonPlan);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    private static async Task SeedLargeScaleFixtureAsync(
        string connString,
        Guid targetSupplierId,
        Guid otherSupplier1,
        Guid otherSupplier2,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();

        Guid sharedOrderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();

        // 1. Insert order header
        await using (var orderCmd = conn.CreateCommand())
        {
            orderCmd.CommandText = """
                INSERT INTO rental.rental_orders (
                    "Id", "OrderCode", "RenterId", "Status", "Currency",
                    "DeliveryMethod", "RecipientName", "RecipientPhone", "DeliveryAddress",
                    "RefundDestinationRef", "TermsVersion", "TermsDocumentRef", "TermsContentHash",
                    "PaymentDeadline", "LatePaymentRefundRequired", "CheckoutOperationId", "CreatedAt", "UpdatedAt", "Version"
                ) VALUES (
                    @id, 'ORD-BULK-001', @renterId, 1, 'VND',
                    'Pickup', 'Test Recipient', '0900000000', 'Test Address',
                    'bank-ref-1', 'v1.0', 'doc-1', 'hash-1',
                    @now, false, 'op-bulk-create', @now, @now, 0
                );
            """;
            orderCmd.Parameters.AddWithValue("id", sharedOrderId);
            orderCmd.Parameters.AddWithValue("renterId", renterId);
            orderCmd.Parameters.AddWithValue("now", fromUtc.AddDays(-5));
            await orderCmd.ExecuteNonQueryAsync();
        }

        // 1b. Mỗi supplier một đơn con trong đơn chung
        Dictionary<Guid, Guid> subOrderIds = new()
        {
            [targetSupplierId] = Guid.NewGuid(),
            [otherSupplier1] = Guid.NewGuid(),
            [otherSupplier2] = Guid.NewGuid()
        };
        int subOrderSequence = 0;
        foreach (var (supplierId, subOrderId) in subOrderIds)
        {
            await using var subOrderCmd = conn.CreateCommand();
            subOrderCmd.CommandText = """
                INSERT INTO rental.rental_sub_orders ("Id", "RentalOrderId", "SupplierId", "SubOrderCode", "CreatedAt")
                VALUES (@id, @orderId, @supplierId, @code, @now);
            """;
            subOrderCmd.Parameters.AddWithValue("id", subOrderId);
            subOrderCmd.Parameters.AddWithValue("orderId", sharedOrderId);
            subOrderCmd.Parameters.AddWithValue("supplierId", supplierId);
            subOrderCmd.Parameters.AddWithValue("code", RentalSubOrder.BuildCode("ORD-BULK-001", ++subOrderSequence));
            subOrderCmd.Parameters.AddWithValue("now", fromUtc.AddDays(-5));
            await subOrderCmd.ExecuteNonQueryAsync();
        }

        // 2. Prepare items and transitions:
        // Total: 10,000 items (1,000 target supplier + 9,000 other suppliers)
        var items = new List<(Guid ItemId, Guid SupplierId)>(10_000);
        var transitions = new List<(Guid Id, Guid OrderId, Guid ItemId, string FromState, string ToState, string Action, Guid? ActorId, int ActorRole, string Reason, DateTimeOffset OccurredAt, string OpId)>(25_000);

        DateTimeOffset midWindow = fromUtc.AddDays(10);
        DateTimeOffset beforeWindow = fromUtc.AddDays(-10);
        DateTimeOffset afterWindow = toUtc.AddDays(10);

        // Target Supplier: 1,000 items
        for (int i = 0; i < 1000; i++)
        {
            Guid itemId = Guid.NewGuid();
            items.Add((itemId, targetSupplierId));

            if (i < 300)
            {
                // 300 items: Accepted inside window (at midWindow)
                transitions.Add((Guid.NewGuid(), sharedOrderId, itemId, "PendingSupplierConfirmation", "AwaitingHandover", "SupplierAccept", targetSupplierId, (int)ActorRole.Supplier, "Acc", midWindow, $"op-acc-{i}"));

                if (i < 50)
                {
                    // Duplicates
                    transitions.Add((Guid.NewGuid(), sharedOrderId, itemId, "PendingSupplierConfirmation", "AwaitingHandover", "SupplierAccept", targetSupplierId, (int)ActorRole.Supplier, "Acc dup", midWindow.AddMinutes(5), $"op-acc-dup-{i}"));
                }
                if (i >= 50 && i < 150)
                {
                    // Subsequent transition to InRental -> Completed
                    transitions.Add((Guid.NewGuid(), sharedOrderId, itemId, "AwaitingHandover", "InRental", "Handover", targetSupplierId, (int)ActorRole.Supplier, "Hand", midWindow.AddDays(1), $"op-hand-{i}"));
                    transitions.Add((Guid.NewGuid(), sharedOrderId, itemId, "InRental", "Completed", "Return", targetSupplierId, (int)ActorRole.Supplier, "Ret", midWindow.AddDays(5), $"op-ret-{i}"));
                }
                if (i >= 150 && i < 190)
                {
                    // 40 items: Cancelled by supplier inside window after accept
                    transitions.Add((Guid.NewGuid(), sharedOrderId, itemId, "AwaitingHandover", "Cancelled", "SupplierCancel", targetSupplierId, (int)ActorRole.Supplier, "Can", midWindow.AddDays(2), $"op-can-{i}"));
                }
            }
            else if (i < 450)
            {
                // 150 items: Rejected inside window
                transitions.Add((Guid.NewGuid(), sharedOrderId, itemId, "PendingSupplierConfirmation", "Rejected", "SupplierReject", targetSupplierId, (int)ActorRole.Supplier, "Rej", midWindow, $"op-rej-{i}"));
                if (i < 320)
                {
                    transitions.Add((Guid.NewGuid(), sharedOrderId, itemId, "PendingSupplierConfirmation", "Rejected", "SupplierReject", targetSupplierId, (int)ActorRole.Supplier, "Rej dup", midWindow.AddMinutes(5), $"op-rej-dup-{i}"));
                }
            }
            else if (i < 530)
            {
                // 80 items: Timeout inside window
                transitions.Add((Guid.NewGuid(), sharedOrderId, itemId, "PendingSupplierConfirmation", "Expired", "SupplierTimeout", null, (int)ActorRole.System, "Timeout", midWindow, $"op-time-{i}"));
                if (i < 460)
                {
                    transitions.Add((Guid.NewGuid(), sharedOrderId, itemId, "PendingSupplierConfirmation", "Expired", "SupplierTimeout", null, (int)ActorRole.System, "Timeout dup", midWindow.AddMinutes(5), $"op-time-dup-{i}"));
                }
            }
            else if (i < 630)
            {
                // 100 items: Accepted OUTSIDE window (beforeWindow)
                transitions.Add((Guid.NewGuid(), sharedOrderId, itemId, "PendingSupplierConfirmation", "AwaitingHandover", "SupplierAccept", targetSupplierId, (int)ActorRole.Supplier, "Acc early", beforeWindow, $"op-acc-early-{i}"));

                if (i < 550)
                {
                    // 20 items: Cancelled by supplier INSIDE window (midWindow) after early accept
                    transitions.Add((Guid.NewGuid(), sharedOrderId, itemId, "AwaitingHandover", "Cancelled", "SupplierCancel", targetSupplierId, (int)ActorRole.Supplier, "Can in window", midWindow, $"op-can-in-{i}"));
                }
            }
            else if (i < 680)
            {
                // 50 items: Cancelled without accept
                transitions.Add((Guid.NewGuid(), sharedOrderId, itemId, "PendingPayment", "Cancelled", "CancelNoAcc", targetSupplierId, (int)ActorRole.Supplier, "Can no acc", midWindow, $"op-can-no-{i}"));
            }
            else if (i < 730)
            {
                // 50 items: Cancelled by Renter (accept was outside window)
                transitions.Add((Guid.NewGuid(), sharedOrderId, itemId, "PendingSupplierConfirmation", "AwaitingHandover", "SupplierAccept", targetSupplierId, (int)ActorRole.Supplier, "Acc", beforeWindow, $"op-acc-r-{i}"));
                transitions.Add((Guid.NewGuid(), sharedOrderId, itemId, "AwaitingHandover", "Cancelled", "RenterCancel", renterId, (int)ActorRole.Renter, "Renter can", midWindow.AddDays(1), $"op-ren-can-{i}"));
            }
            else if (i < 780)
            {
                // 50 items: Cancelled by Admin (accept was outside window)
                transitions.Add((Guid.NewGuid(), sharedOrderId, itemId, "PendingSupplierConfirmation", "AwaitingHandover", "SupplierAccept", targetSupplierId, (int)ActorRole.Supplier, "Acc", beforeWindow, $"op-acc-adm-{i}"));
                transitions.Add((Guid.NewGuid(), sharedOrderId, itemId, "AwaitingHandover", "Cancelled", "AdminCancel", Guid.NewGuid(), (int)ActorRole.Admin, "Admin can", midWindow.AddDays(1), $"op-adm-can-{i}"));
            }
            else
            {
                // 220 items: Subsequent or external
                transitions.Add((Guid.NewGuid(), sharedOrderId, itemId, "PendingSupplierConfirmation", "AwaitingHandover", "SupplierAccept", targetSupplierId, (int)ActorRole.Supplier, "Acc late", afterWindow, $"op-acc-late-{i}"));
            }
        }

        // Other Suppliers: 9,000 items
        for (int i = 0; i < 9000; i++)
        {
            Guid itemId = Guid.NewGuid();
            Guid supp = (i % 2 == 0) ? otherSupplier1 : otherSupplier2;
            items.Add((itemId, supp));

            // Various transitions
            transitions.Add((Guid.NewGuid(), sharedOrderId, itemId, "PendingSupplierConfirmation", "AwaitingHandover", "SupplierAccept", supp, (int)ActorRole.Supplier, "Other acc", midWindow, $"op-oth-acc-{i}"));
            if (i % 5 == 0)
            {
                transitions.Add((Guid.NewGuid(), sharedOrderId, itemId, "AwaitingHandover", "Cancelled", "SupplierCancel", supp, (int)ActorRole.Supplier, "Other can", midWindow.AddDays(1), $"op-oth-can-{i}"));
            }
        }

        // Bulk copy items
        await using (var writer = await conn.BeginBinaryImportAsync("""
            COPY rental.rental_items (
                "Id", "RentalOrderId", "EquipmentListingId", "SupplierId", "Quantity",
                "ExpectedStartDate", "ExpectedEndDate", "ListingTitleSnapshot",
                "ListingDescriptionSnapshot", "PickupLocationSnapshot", "EquipmentTypeId",
                "EquipmentTypeNameSnapshot", "BrandId", "BrandNameSnapshot", "ModelSnapshot",
                "SpecSchemaVersionSnapshot", "SpecValuesSnapshot", "ThumbnailMediaIdSnapshot",
                "DailyRateSnapshot", "DepositPerUnitSnapshot",
                "RentalAmount", "DepositAmount", "Status", "AllocationGroupId", "RentalSubOrderId"
            ) FROM STDIN (FORMAT BINARY)
        """))
        {
            foreach (var (itemId, supplierId) in items)
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(itemId, NpgsqlDbType.Uuid);
                await writer.WriteAsync(sharedOrderId, NpgsqlDbType.Uuid);
                await writer.WriteAsync(Guid.NewGuid(), NpgsqlDbType.Uuid);
                await writer.WriteAsync(supplierId, NpgsqlDbType.Uuid);
                await writer.WriteAsync(1, NpgsqlDbType.Integer);
                await writer.WriteAsync(new DateOnly(2026, 10, 10), NpgsqlDbType.Date);
                await writer.WriteAsync(new DateOnly(2026, 10, 15), NpgsqlDbType.Date);
                await writer.WriteAsync("Bulk Item", NpgsqlDbType.Varchar);
                await writer.WriteNullAsync();
                await writer.WriteAsync("Location", NpgsqlDbType.Varchar);
                await writer.WriteAsync(TestListingSnapshots.Default.EquipmentTypeId, NpgsqlDbType.Uuid);
                await writer.WriteAsync(TestListingSnapshots.Default.EquipmentTypeName, NpgsqlDbType.Varchar);
                await writer.WriteAsync(TestListingSnapshots.Default.BrandId, NpgsqlDbType.Uuid);
                await writer.WriteAsync(TestListingSnapshots.Default.BrandName, NpgsqlDbType.Varchar);
                await writer.WriteAsync(TestListingSnapshots.Default.Model, NpgsqlDbType.Varchar);
                await writer.WriteAsync(TestListingSnapshots.Default.SpecSchemaVersion, NpgsqlDbType.Integer);
                await writer.WriteAsync(TestListingSnapshots.Default.SpecValuesJson, NpgsqlDbType.Jsonb);
                await writer.WriteNullAsync();
                await writer.WriteAsync(100_000m, NpgsqlDbType.Numeric);
                await writer.WriteAsync(200_000m, NpgsqlDbType.Numeric);
                await writer.WriteAsync(500_000m, NpgsqlDbType.Numeric);
                await writer.WriteAsync(200_000m, NpgsqlDbType.Numeric);
                await writer.WriteAsync((int)RentalItemStatus.PendingSupplierConfirmation, NpgsqlDbType.Integer);
                await writer.WriteAsync($"grp-{itemId:N}", NpgsqlDbType.Varchar);
                await writer.WriteAsync(subOrderIds[supplierId], NpgsqlDbType.Uuid);
            }
            await writer.CompleteAsync();
        }

        // Bulk copy transitions
        await using (var writer = await conn.BeginBinaryImportAsync("""
            COPY rental.rental_state_transitions (
                "Id", "RentalOrderId", "RentalItemId", "FromState", "ToState",
                "Action", "ActorId", "ActorRole", "Reason", "OccurredAt", "OperationId"
            ) FROM STDIN (FORMAT BINARY)
        """))
        {
            foreach (var t in transitions)
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(t.Id, NpgsqlDbType.Uuid);
                await writer.WriteAsync(t.OrderId, NpgsqlDbType.Uuid);
                await writer.WriteAsync(t.ItemId, NpgsqlDbType.Uuid);
                await writer.WriteAsync(t.FromState, NpgsqlDbType.Varchar);
                await writer.WriteAsync(t.ToState, NpgsqlDbType.Varchar);
                await writer.WriteAsync(t.Action, NpgsqlDbType.Varchar);
                if (t.ActorId.HasValue)
                {
                    await writer.WriteAsync(t.ActorId.Value, NpgsqlDbType.Uuid);
                }
                else
                {
                    await writer.WriteNullAsync();
                }
                await writer.WriteAsync(t.ActorRole, NpgsqlDbType.Integer);
                await writer.WriteAsync(t.Reason, NpgsqlDbType.Varchar);
                await writer.WriteAsync(t.OccurredAt, NpgsqlDbType.TimestampTz);
                await writer.WriteAsync(t.OpId, NpgsqlDbType.Varchar);
            }
            await writer.CompleteAsync();
        }

        // Run ANALYZE to refresh statistics for PostgreSQL query planner
        await using var analyzeCmd = conn.CreateCommand();
        analyzeCmd.CommandText = "ANALYZE rental.rental_items; ANALYZE rental.rental_state_transitions;";
        await analyzeCmd.ExecuteNonQueryAsync();
    }
}
