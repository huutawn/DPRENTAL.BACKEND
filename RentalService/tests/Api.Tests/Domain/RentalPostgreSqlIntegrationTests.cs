using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Domain;

public sealed class RentalPostgreSqlIntegrationTests
{
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
        string dbName = $"rental_tests_{Guid.NewGuid():N}";
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

    [Fact]
    public async Task PostgreSql_EnsureCreated_CreatesAllTablesAndUniqueConstraints()
    {
        if (!await IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available at 127.0.0.1:5433.");
        }

        string connString = await CreateTestDatabaseAsync();
        try
        {
            var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
                .UseNpgsql(connString)
                .Options;

            await using var context = new RentalServiceDbContext(options);
            bool created = await context.Database.EnsureCreatedAsync();
            Assert.True(created);

            // Verify tables in rental schema exist
            await using var conn = new NpgsqlConnection(connString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT table_name
                FROM information_schema.tables
                WHERE table_schema = 'rental'
                ORDER BY table_name;";

            List<string> tables = [];
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }

            Assert.Contains("rental_orders", tables);
            Assert.Contains("rental_items", tables);
            Assert.Contains("rental_unit_assignments", tables);
            Assert.Contains("rental_operation_receipts", tables);
            Assert.Contains("rental_state_transitions", tables);
            Assert.Contains("rental_carts", tables);
            Assert.Contains("rental_cart_items", tables);
            Assert.Contains("handover_records", tables);
            Assert.Contains("return_records", tables);
            Assert.Contains("deduction_proposals", tables);
            Assert.Contains("disputes", tables);
            Assert.Contains("rental_terms_acceptances", tables);
            Assert.Contains("rental_inbox_messages", tables);
            Assert.Contains("rental_outbox_messages", tables);
            Assert.Contains("rental_sub_orders", tables);
            Assert.Contains("rental_checkout_operations", tables);
            Assert.Equal(16, tables.Count);
        }
        finally
        {
            await DropTestDatabaseAsync(connString);
        }
    }

    [Fact]
    public async Task PostgreSql_PersistsAndReloads_RentalOrderAggregateWithReceiptsAndAudit()
    {
        if (!await IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available at 127.0.0.1:5433.");
        }

        string connString = await CreateTestDatabaseAsync();
        try
        {
            var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
                .UseNpgsql(connString)
                .Options;

            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid supplierId = Guid.NewGuid();
            Guid itemId = Guid.NewGuid();
            Guid unitId = Guid.NewGuid();
            DateTimeOffset now = DateTimeOffset.UtcNow;

            // 1. Arrange & Mutate aggregate
            InitialRentalUnitAssignmentDto assignmentDto = new(unitId, Guid.NewGuid(), Guid.NewGuid());
            InitialRentalItemDto itemDto = new(
                itemId,
                Guid.NewGuid(),
                supplierId,
                1,
                new(2026, 10, 10),
                new(2026, 10, 15),
                "Sony FX3 Camera Kit",
                "Professional Camera Kit",
                "District 1, HCMC",
                200_000m,
                500_000m,
                "group-01",
                [assignmentDto],
            TestListingSnapshots.Default);

            RentalOrder order = RentalOrder.Create(
                orderId,
                "ORD-PG-001",
                renterId,
                "VND",
                "Pickup",
                "Customer A",
                "0909123456",
                "123 Nguyen Hue",
                "Call before delivery",
                "bank-ref-001",
                "v1.0",
                "terms-doc-001",
                "sha256-hash-001",
                "op-create-pg",
                now,
                [itemDto]).Value;

            RentalItem item = order.Items.First();
            RentalUnitAssignment unit = item.UnitAssignments.First();

            Guid handoverMediaId = Guid.NewGuid();
            unit.RecordHandover(Guid.NewGuid(), UnitCondition.Normal, "Good condition", [handoverMediaId]);
            HandoverRecord handover = new(
                Guid.NewGuid(),
                itemId,
                now.AddDays(1),
                "Handed over to renter",
                supplierId,
                now.AddDays(1),
                now.AddDays(1));
            handover.AddAssignment(unit);
            item.SetHandoverRecord(handover);

            Guid returnMediaId = Guid.NewGuid();
            unit.RecordReturn(Guid.NewGuid(), UnitCondition.Damaged, "Lens scratch", [returnMediaId], null, null);
            ReturnRecord returnRecord = new(
                Guid.NewGuid(),
                itemId,
                now.AddDays(5),
                "Return with damage",
                supplierId,
                now.AddDays(5));
            returnRecord.AddAssignment(unit);
            item.AddReturnRecord(returnRecord);

            Guid deductionMediaId = Guid.NewGuid();
            Guid propId = Guid.NewGuid();
            DeductionProposal proposal = new(
                propId,
                itemId,
                supplierId,
                now.AddDays(5),
                now.AddDays(7), 150_000m, "Lens polishing fee", [deductionMediaId]);
            unit.RecordDeduction(propId, "Lens polishing fee", [deductionMediaId]);
            proposal.AddAssignment(unit);

            Guid disputeMediaId = Guid.NewGuid();
            Dispute dispute = proposal.OpenDispute(
                Guid.NewGuid(),
                renterId,
                "Scratch was pre-existing",
                [disputeMediaId],
                now.AddDays(5).AddHours(2));

            Guid adminId = Guid.NewGuid();
            Guid resolutionMediaId = Guid.NewGuid();
            dispute.Resolve(
                100_000m,
                "Settled at 100,000 VND",
                adminId,
                now.AddDays(5).AddHours(3),
                [resolutionMediaId]);
            proposal.ApplyResolution();
            item.SetDeductionProposal(proposal);

            order.AddOperationReceipt(new RentalOperationReceipt(Guid.NewGuid(), orderId, "op-acc", "SupplierAccept", "f-acc", true, null, null, now));
            order.AddOperationReceipt(new RentalOperationReceipt(Guid.NewGuid(), orderId, "op-resolve", "ResolveDispute", "f-res", true, null, null, now));

            order.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), orderId, itemId, "PendingPayment", "PendingSupplierConfirmation", "InvoicePaid", renterId, ActorRole.System, "Paid", now, "op-pay"));
            order.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), orderId, itemId, "PendingSupplierConfirmation", "AwaitingHandover", "SupplierAccept", supplierId, ActorRole.Supplier, "Accepted", now, "op-acc"));
            order.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), orderId, itemId, "AwaitingHandover", "InRental", "SubmitHandover", supplierId, ActorRole.Supplier, "Handover", now, "op-hand"));
            order.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), orderId, itemId, "InRental", "PendingSettlement", "SubmitReturn", supplierId, ActorRole.Supplier, "Return", now, "op-ret"));
            order.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), orderId, itemId, "PendingSettlement", "PendingSettlement", "ResolveDispute", adminId, ActorRole.Admin, "Dispute resolved", now, "op-resolve"));
            // 2. Persist in PostgreSQL
            await using (var writeCtx = new RentalServiceDbContext(options))
            {
                await writeCtx.Database.EnsureCreatedAsync();
                writeCtx.RentalOrders.Add(order);
                await writeCtx.SaveChangesAsync();
            }

            // 3. Read back in fresh context
            await using (var readCtx = new RentalServiceDbContext(options))
            {
                RentalOrder? reloaded = await readCtx.RentalOrders
                    .Include(o => o.TermsAcceptance)
                    .Include(o => o.Items)
                        .ThenInclude(i => i.UnitAssignments)
                    .Include(o => o.Items)
                        .ThenInclude(i => i.HandoverRecord)
                    .Include(o => o.Items)
                        .ThenInclude(i => i.ReturnRecords)
                    .Include(o => o.Items)
                        .ThenInclude(i => i.DeductionProposal!)
                            .ThenInclude(p => p.Dispute)
                    .Include(o => o.StateTransitions)
                    .Include(o => o.OperationReceipts)
                    .FirstOrDefaultAsync(o => o.Id == orderId);

                Assert.NotNull(reloaded);
                Assert.Equal("ORD-PG-001", reloaded.OrderCode);
                Assert.Equal(RentalOrderStatus.PendingPayment, reloaded.Status);
                Assert.Single(reloaded.Items);

                RentalItem reloadedItem = reloaded.Items.First();
                Assert.NotNull(reloadedItem.DeductionProposal?.Dispute);
                Assert.Equal(150_000m, reloadedItem.DeductionProposal.TotalProposedAmount);
                Assert.Contains(deductionMediaId, reloadedItem.DeductionProposal.EvidenceMediaIds);
                Assert.Equal(100_000m, reloadedItem.DeductionProposal.Dispute.FinalDeductionAmount);

                RentalUnitAssignment reloadedUnit = reloadedItem.UnitAssignments.First();
                Assert.Equal(RentalUnitAssignmentStatus.ReturnedDamaged, reloadedUnit.Status);

                // Verify encapsulated Collections and backing fields in PostgreSQL
                Assert.Contains(handoverMediaId, reloadedUnit.HandoverMediaIds);
                Assert.Contains(returnMediaId, reloadedUnit.ReturnMediaIds);
                Assert.Contains(deductionMediaId, reloadedUnit.DeductionMediaIds);

                Assert.NotNull(reloadedItem.DeductionProposal?.Dispute);
                Assert.Contains(disputeMediaId, reloadedItem.DeductionProposal.Dispute.MediaIds);
                Assert.Contains(resolutionMediaId, reloadedItem.DeductionProposal.Dispute.ResolutionMediaIds);

                // Verify operation receipts
                Assert.Contains(reloaded.OperationReceipts, r => r.OperationId == "op-acc" && r.Action == "SupplierAccept");
                Assert.Contains(reloaded.OperationReceipts, r => r.OperationId == "op-resolve" && r.Action == "ResolveDispute");

                // Verify state transitions
                Assert.True(reloaded.StateTransitions.Count >= 5);
            }
        }
        finally
        {
            await DropTestDatabaseAsync(connString);
        }
    }

    [Fact]
    public async Task PostgreSql_UniqueConstraint_PreventsDuplicateOperationReceipt()
    {
        if (!await IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available at 127.0.0.1:5433.");
        }

        string connString = await CreateTestDatabaseAsync();
        try
        {
            var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
                .UseNpgsql(connString)
                .Options;

            await using var context = new RentalServiceDbContext(options);
            await context.Database.EnsureCreatedAsync();

            Guid orderId = Guid.NewGuid();
            Guid unitId = Guid.NewGuid();
            InitialRentalUnitAssignmentDto assignmentDto = new(unitId, Guid.NewGuid(), Guid.NewGuid());
            InitialRentalItemDto itemDto = new(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                new(2026, 10, 10),
                new(2026, 10, 15),
                "Title",
                "Desc",
                "Loc",
                100_000m,
                200_000m,
                "grp",
                [assignmentDto],
            TestListingSnapshots.Default);

            RentalOrder order = RentalOrder.Create(
                orderId,
                "ORD-UNIQUE-001",
                Guid.NewGuid(),
                "VND",
                "Pickup",
                "Cust",
                "0901234567",
                "Addr",
                null,
                "refund-ref",
                "v1",
                "doc",
                "hash",
                "op-create",
                DateTimeOffset.UtcNow,
                [itemDto]).Value;

            context.RentalOrders.Add(order);
            await context.SaveChangesAsync();

            RentalOperationReceipt r1 = new(Guid.NewGuid(), orderId, "op-unique", "ActionA", "hash1", true, null, null, DateTimeOffset.UtcNow);
            context.RentalOperationReceipts.Add(r1);
            await context.SaveChangesAsync();

            await using var duplicateCtx = new RentalServiceDbContext(options);
            RentalOperationReceipt r2 = new(Guid.NewGuid(), orderId, "op-unique", "ActionB", "hash2", true, null, null, DateTimeOffset.UtcNow);
            duplicateCtx.RentalOperationReceipts.Add(r2);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => duplicateCtx.SaveChangesAsync());
            var pgEx = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, pgEx.SqlState);
            Assert.Contains("IX_rental_operation_receipts_RentalOrderId_OperationId", pgEx.ConstraintName ?? string.Empty);

            // Verify rollback: only r1 exists in the database
            await using var verifyCtx = new RentalServiceDbContext(options);
            var receipts = await verifyCtx.RentalOperationReceipts
                .Where(r => r.RentalOrderId == orderId && r.OperationId == "op-unique")
                .ToListAsync();
            Assert.Single(receipts);
            Assert.Equal("ActionA", receipts[0].Action);
        }
        finally
        {
            await DropTestDatabaseAsync(connString);
        }
    }
}
