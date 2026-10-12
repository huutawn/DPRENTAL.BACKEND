using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Npgsql;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Domain;

public sealed class SupplierConfirmationStatisticsCountingPostgreSqlTests
{
    private static async Task EnsurePostgreSqlAvailableAsync()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available at test connection string.");
        }
    }

    [Fact]
    public void BuildDatabaseConnectionString_PreservesCustomHostPortUserAndOptions()
    {
        string baseConn = "Host=mycustomhost.internal;Port=5439;Database=template1;Username=custom_admin;Password=secret123!;SslMode=Require;Include Error Detail=true";
        string targetDb = "test_custom_db";

        string result = PostgresTestDatabaseHelper.BuildDatabaseConnectionString(baseConn, targetDb);

        var builder = new NpgsqlConnectionStringBuilder(result);
        Assert.Equal("mycustomhost.internal", builder.Host);
        Assert.Equal(5439, builder.Port);
        Assert.Equal("custom_admin", builder.Username);
        Assert.Equal("secret123!", builder.Password);
        Assert.Equal(SslMode.Require, builder.SslMode);
        Assert.True(builder.IncludeErrorDetail);
        Assert.Equal("test_custom_db", builder.Database);
    }

    [Fact]
    public async Task GetSupplierConfirmationStatisticsAsync_WhenNoHistory_ReturnsAllZeros()
    {
        await EnsurePostgreSqlAvailableAsync();

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using var ctx = new RentalServiceDbContext(options);
            await ctx.Database.EnsureCreatedAsync();

            var repo = new RentalOrderRepository(ctx);
            var stats = await repo.GetSupplierConfirmationStatisticsAsync(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddDays(-7),
                DateTimeOffset.UtcNow);

            Assert.Equal(0, stats.AcceptedCount);
            Assert.Equal(0, stats.RejectedCount);
            Assert.Equal(0, stats.ConfirmationTimeoutCount);
            Assert.Equal(0, stats.CancelAfterAcceptCount);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task GetSupplierConfirmationStatisticsAsync_MultipleSuppliersInSameOrder_DoNotMixCounts()
    {
        await EnsurePostgreSqlAvailableAsync();

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using var ctx = new RentalServiceDbContext(options);
            await ctx.Database.EnsureCreatedAsync();

            Guid supplierA = Guid.NewGuid();
            Guid supplierB = Guid.NewGuid();
            Guid orderId = Guid.NewGuid();
            Guid itemA = Guid.NewGuid();
            Guid itemB = Guid.NewGuid();

            var fromUtc = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
            var toUtc = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
            var eventTime = new DateTimeOffset(2026, 10, 10, 10, 0, 0, TimeSpan.Zero);

            var itemADto = new InitialRentalItemDto(
                itemA,
                Guid.NewGuid(),
                supplierA,
                1,
                new(2026, 10, 10),
                new(2026, 10, 15),
                "Item Supplier A",
                null,
                "District 1",
                100_000m,
                200_000m,
                "group-a",
                [new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())]);

            var itemBDto = new InitialRentalItemDto(
                itemB,
                Guid.NewGuid(),
                supplierB,
                1,
                new(2026, 10, 10),
                new(2026, 10, 15),
                "Item Supplier B",
                null,
                "District 1",
                100_000m,
                200_000m,
                "group-b",
                [new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())]);

            var order = RentalOrder.Create(
                orderId,
                "ORD-MIX-001",
                Guid.NewGuid(),
                "VND",
                "Pickup",
                "Customer",
                "0909123456",
                "Address",
                null,
                "ref",
                "v1",
                "doc",
                "hash",
                "op-mix",
                eventTime.AddHours(-1),
                [itemADto, itemBDto]).Value;

            order.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(),
                orderId,
                itemA,
                "PendingSupplierConfirmation",
                "AwaitingHandover",
                "SupplierAccept",
                supplierA,
                ActorRole.Supplier,
                "Accepted",
                eventTime,
                "op-acc-a"));

            ctx.RentalOrders.Add(order);
            await ctx.SaveChangesAsync();

            var repo = new RentalOrderRepository(ctx);
            var statsA = await repo.GetSupplierConfirmationStatisticsAsync(supplierA, fromUtc, toUtc);
            var statsB = await repo.GetSupplierConfirmationStatisticsAsync(supplierB, fromUtc, toUtc);

            Assert.Equal(1, statsA.AcceptedCount);
            Assert.Equal(0, statsA.RejectedCount);
            Assert.Equal(0, statsA.ConfirmationTimeoutCount);
            Assert.Equal(0, statsA.CancelAfterAcceptCount);

            Assert.Equal(0, statsB.AcceptedCount);
            Assert.Equal(0, statsB.RejectedCount);
            Assert.Equal(0, statsB.ConfirmationTimeoutCount);
            Assert.Equal(0, statsB.CancelAfterAcceptCount);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task GetSupplierConfirmationStatisticsAsync_QuantityGreaterThanOne_CountsAsOneRentalItem()
    {
        await EnsurePostgreSqlAvailableAsync();

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using var ctx = new RentalServiceDbContext(options);
            await ctx.Database.EnsureCreatedAsync();

            Guid supplierId = Guid.NewGuid();
            Guid orderId = Guid.NewGuid();
            Guid itemId = Guid.NewGuid();
            var eventTime = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

            var order = PostgresTestDatabaseHelper.CreateOrderWithItem(orderId, itemId, supplierId, quantity: 5, eventTime.AddHours(-2));

            order.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(),
                orderId,
                itemId,
                "PendingSupplierConfirmation",
                "AwaitingHandover",
                "SupplierAccept",
                supplierId,
                ActorRole.Supplier,
                "Accepted all 5 units",
                eventTime,
                "op-acc"));

            ctx.RentalOrders.Add(order);
            await ctx.SaveChangesAsync();

            var repo = new RentalOrderRepository(ctx);
            var stats = await repo.GetSupplierConfirmationStatisticsAsync(
                supplierId,
                new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));

            Assert.Equal(1, stats.AcceptedCount);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task GetSupplierConfirmationStatisticsAsync_ItemCompleted_StillCountsAcceptedFromHistory()
    {
        await EnsurePostgreSqlAvailableAsync();

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using var ctx = new RentalServiceDbContext(options);
            await ctx.Database.EnsureCreatedAsync();

            Guid supplierId = Guid.NewGuid();
            Guid orderId = Guid.NewGuid();
            Guid itemId = Guid.NewGuid();
            var acceptTime = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

            var order = PostgresTestDatabaseHelper.CreateOrderWithItem(orderId, itemId, supplierId, 1, acceptTime.AddHours(-2));

            order.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), orderId, itemId, "PendingSupplierConfirmation", "AwaitingHandover", "SupplierAccept", supplierId, ActorRole.Supplier, "Accepted", acceptTime, "op-acc"));
            order.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), orderId, itemId, "AwaitingHandover", "InRental", "Handover", supplierId, ActorRole.Supplier, "Handover", acceptTime.AddDays(1), "op-hand"));
            order.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), orderId, itemId, "InRental", "Completed", "Return", supplierId, ActorRole.Supplier, "Return complete", acceptTime.AddDays(5), "op-comp"));

            ctx.RentalOrders.Add(order);
            await ctx.SaveChangesAsync();

            var repo = new RentalOrderRepository(ctx);
            var stats = await repo.GetSupplierConfirmationStatisticsAsync(
                supplierId,
                new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));

            Assert.Equal(1, stats.AcceptedCount);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task GetSupplierConfirmationStatisticsAsync_RejectCoverage_ValidRejectCounted_WrongRoleOrActorExcluded()
    {
        await EnsurePostgreSqlAvailableAsync();

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using var ctx = new RentalServiceDbContext(options);
            await ctx.Database.EnsureCreatedAsync();

            Guid supplierId = Guid.NewGuid();
            var eventTime = new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

            // Item 1: Valid SupplierReject -> counted in rejectedCount
            Guid o1 = Guid.NewGuid();
            Guid i1 = Guid.NewGuid();
            var order1 = PostgresTestDatabaseHelper.CreateOrderWithItem(o1, i1, supplierId, 1, eventTime.AddHours(-1));
            order1.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), o1, i1, "PendingSupplierConfirmation", "Rejected", "SupplierReject", supplierId, ActorRole.Supplier, "Valid reject", eventTime, "op-rej-1"));

            // Item 2: Action is SupplierReject but ActorRole is System -> NOT counted
            Guid o2 = Guid.NewGuid();
            Guid i2 = Guid.NewGuid();
            var order2 = PostgresTestDatabaseHelper.CreateOrderWithItem(o2, i2, supplierId, 1, eventTime.AddHours(-1));
            order2.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), o2, i2, "PendingSupplierConfirmation", "Rejected", "SupplierReject", supplierId, ActorRole.System, "Wrong role", eventTime, "op-rej-2"));

            // Item 3: ActorRole is Supplier but ActorId is someone else -> NOT counted
            Guid o3 = Guid.NewGuid();
            Guid i3 = Guid.NewGuid();
            var order3 = PostgresTestDatabaseHelper.CreateOrderWithItem(o3, i3, supplierId, 1, eventTime.AddHours(-1));
            order3.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), o3, i3, "PendingSupplierConfirmation", "Rejected", "SupplierReject", Guid.NewGuid(), ActorRole.Supplier, "Wrong actor id", eventTime, "op-rej-3"));

            ctx.RentalOrders.AddRange(order1, order2, order3);
            await ctx.SaveChangesAsync();

            var repo = new RentalOrderRepository(ctx);
            var stats = await repo.GetSupplierConfirmationStatisticsAsync(
                supplierId,
                new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));

            Assert.Equal(1, stats.RejectedCount);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task GetSupplierConfirmationStatisticsAsync_OwnershipCoverage_DifferentSupplierActorIdExcluded()
    {
        await EnsurePostgreSqlAvailableAsync();

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using var ctx = new RentalServiceDbContext(options);
            await ctx.Database.EnsureCreatedAsync();

            Guid ownerSupplierId = Guid.NewGuid();
            Guid imposterSupplierId = Guid.NewGuid();
            var eventTime = new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

            // Item owned by ownerSupplierId, but transition carries ActorId of imposterSupplierId
            Guid o1 = Guid.NewGuid();
            Guid i1 = Guid.NewGuid();
            var order = PostgresTestDatabaseHelper.CreateOrderWithItem(o1, i1, ownerSupplierId, 1, eventTime.AddHours(-1));

            // Imposter tries to accept
            order.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), o1, i1, "PendingSupplierConfirmation", "AwaitingHandover", "SupplierAccept", imposterSupplierId, ActorRole.Supplier, "Imposter accept", eventTime, "op-imp-acc"));

            // Imposter tries to cancel
            order.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), o1, i1, "AwaitingHandover", "Cancelled", "SupplierCancel", imposterSupplierId, ActorRole.Supplier, "Imposter cancel", eventTime.AddHours(1), "op-imp-can"));

            ctx.RentalOrders.Add(order);
            await ctx.SaveChangesAsync();

            var repo = new RentalOrderRepository(ctx);
            var statsOwner = await repo.GetSupplierConfirmationStatisticsAsync(
                ownerSupplierId,
                new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));

            Assert.Equal(0, statsOwner.AcceptedCount);
            Assert.Equal(0, statsOwner.CancelAfterAcceptCount);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task GetSupplierConfirmationStatisticsAsync_CancellationCoverage_AdminSystemRenterAndPriorToAcceptExcluded()
    {
        await EnsurePostgreSqlAvailableAsync();

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using var ctx = new RentalServiceDbContext(options);
            await ctx.Database.EnsureCreatedAsync();

            Guid supplierId = Guid.NewGuid();
            var eventTime = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

            // Case 1: Valid Supplier cancel after accept -> counted
            Guid o1 = Guid.NewGuid();
            Guid i1 = Guid.NewGuid();
            var order1 = PostgresTestDatabaseHelper.CreateOrderWithItem(o1, i1, supplierId, 1, eventTime.AddHours(-5));
            order1.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), o1, i1, "PendingSupplierConfirmation", "AwaitingHandover", "SupplierAccept", supplierId, ActorRole.Supplier, "Accepted", eventTime.AddHours(-3), "op-acc1"));
            order1.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), o1, i1, "AwaitingHandover", "Cancelled", "SupplierCancel", supplierId, ActorRole.Supplier, "Canceled by supplier", eventTime, "op-can1"));

            // Case 2: Admin cancels after accept -> NOT counted
            Guid o2 = Guid.NewGuid();
            Guid i2 = Guid.NewGuid();
            var order2 = PostgresTestDatabaseHelper.CreateOrderWithItem(o2, i2, supplierId, 1, eventTime.AddHours(-5));
            order2.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), o2, i2, "PendingSupplierConfirmation", "AwaitingHandover", "SupplierAccept", supplierId, ActorRole.Supplier, "Accepted", eventTime.AddHours(-3), "op-acc2"));
            order2.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), o2, i2, "AwaitingHandover", "Cancelled", "AdminCancel", Guid.NewGuid(), ActorRole.Admin, "Admin force cancel", eventTime, "op-adm-can"));

            // Case 3: System cancels after accept -> NOT counted
            Guid o3 = Guid.NewGuid();
            Guid i3 = Guid.NewGuid();
            var order3 = PostgresTestDatabaseHelper.CreateOrderWithItem(o3, i3, supplierId, 1, eventTime.AddHours(-5));
            order3.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), o3, i3, "PendingSupplierConfirmation", "AwaitingHandover", "SupplierAccept", supplierId, ActorRole.Supplier, "Accepted", eventTime.AddHours(-3), "op-acc3"));
            order3.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), o3, i3, "AwaitingHandover", "Cancelled", "SystemCancel", null, ActorRole.System, "System auto cancel", eventTime, "op-sys-can"));

            // Case 4: Renter cancels after accept -> NOT counted
            Guid o4 = Guid.NewGuid();
            Guid i4 = Guid.NewGuid();
            var order4 = PostgresTestDatabaseHelper.CreateOrderWithItem(o4, i4, supplierId, 1, eventTime.AddHours(-5));
            order4.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), o4, i4, "PendingSupplierConfirmation", "AwaitingHandover", "SupplierAccept", supplierId, ActorRole.Supplier, "Accepted", eventTime.AddHours(-3), "op-acc4"));
            order4.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), o4, i4, "AwaitingHandover", "Cancelled", "RenterCancel", Guid.NewGuid(), ActorRole.Renter, "Renter cancel", eventTime, "op-ren-can"));

            // Case 5: Supplier cancels BEFORE accept -> NOT counted
            Guid o5 = Guid.NewGuid();
            Guid i5 = Guid.NewGuid();
            var order5 = PostgresTestDatabaseHelper.CreateOrderWithItem(o5, i5, supplierId, 1, eventTime.AddHours(-5));
            order5.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), o5, i5, "PendingSupplierConfirmation", "Cancelled", "SupplierCancel", supplierId, ActorRole.Supplier, "Cancel directly", eventTime, "op-direct-can"));

            ctx.RentalOrders.AddRange(order1, order2, order3, order4, order5);
            await ctx.SaveChangesAsync();

            var repo = new RentalOrderRepository(ctx);
            var stats = await repo.GetSupplierConfirmationStatisticsAsync(
                supplierId,
                new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));

            Assert.Equal(4, stats.AcceptedCount); // order 1, 2, 3, 4 were accepted
            Assert.Equal(1, stats.CancelAfterAcceptCount); // ONLY order 1 qualifies as supplier cancel after accept
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task GetSupplierConfirmationStatisticsAsync_DedupCoverage_RejectTimeoutCancelDuplicatesCountOnce()
    {
        await EnsurePostgreSqlAvailableAsync();

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using var ctx = new RentalServiceDbContext(options);
            await ctx.Database.EnsureCreatedAsync();

            Guid supplierId = Guid.NewGuid();
            var eventTime = new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);

            // Item 1: Duplicate Reject
            Guid o1 = Guid.NewGuid();
            Guid i1 = Guid.NewGuid();
            var order1 = PostgresTestDatabaseHelper.CreateOrderWithItem(o1, i1, supplierId, 1, eventTime.AddHours(-2));
            order1.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), o1, i1, "PendingSupplierConfirmation", "Rejected", "SupplierReject", supplierId, ActorRole.Supplier, "Reject 1", eventTime, "op-r1"));
            order1.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), o1, i1, "PendingSupplierConfirmation", "Rejected", "SupplierReject", supplierId, ActorRole.Supplier, "Reject 2 duplicate", eventTime.AddMinutes(5), "op-r2"));

            // Item 2: Duplicate Timeout
            Guid o2 = Guid.NewGuid();
            Guid i2 = Guid.NewGuid();
            var order2 = PostgresTestDatabaseHelper.CreateOrderWithItem(o2, i2, supplierId, 1, eventTime.AddHours(-2));
            order2.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), o2, i2, "PendingSupplierConfirmation", "Expired", "SupplierTimeout", null, ActorRole.System, "Timeout 1", eventTime, "op-t1"));
            order2.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), o2, i2, "PendingSupplierConfirmation", "Expired", "SupplierTimeout", null, ActorRole.System, "Timeout 2 duplicate", eventTime.AddMinutes(5), "op-t2"));

            // Item 3: Duplicate Cancel after accept
            Guid o3 = Guid.NewGuid();
            Guid i3 = Guid.NewGuid();
            var order3 = PostgresTestDatabaseHelper.CreateOrderWithItem(o3, i3, supplierId, 1, eventTime.AddHours(-2));
            order3.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), o3, i3, "PendingSupplierConfirmation", "AwaitingHandover", "SupplierAccept", supplierId, ActorRole.Supplier, "Accept", eventTime.AddHours(-1), "op-a1"));
            order3.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), o3, i3, "AwaitingHandover", "Cancelled", "SupplierCancel", supplierId, ActorRole.Supplier, "Cancel 1", eventTime, "op-c1"));
            order3.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), o3, i3, "AwaitingHandover", "Cancelled", "SupplierCancel", supplierId, ActorRole.Supplier, "Cancel 2 duplicate", eventTime.AddMinutes(5), "op-c2"));
            ctx.RentalOrders.AddRange(order1, order2, order3);
            await ctx.SaveChangesAsync();

            var repo = new RentalOrderRepository(ctx);
            var stats = await repo.GetSupplierConfirmationStatisticsAsync(
                supplierId,
                new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));

            Assert.Equal(1, stats.RejectedCount);
            Assert.Equal(1, stats.ConfirmationTimeoutCount);
            Assert.Equal(1, stats.CancelAfterAcceptCount);
            Assert.Equal(1, stats.AcceptedCount);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task GetSupplierConfirmationStatisticsAsync_TimeWindowBoundaries_AllFourMetrics_FromUtcIncluded_ToUtcExcluded()
    {
        await EnsurePostgreSqlAvailableAsync();

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using var ctx = new RentalServiceDbContext(options);
            await ctx.Database.EnsureCreatedAsync();

            Guid supplierId = Guid.NewGuid();
            var fromUtc = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
            var toUtc = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

            // Item 1: Accept at fromUtc (included)
            Guid o1 = Guid.NewGuid(); Guid i1 = Guid.NewGuid();
            var order1 = PostgresTestDatabaseHelper.CreateOrderWithItem(o1, i1, supplierId, 1, fromUtc.AddHours(-1));
            order1.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), o1, i1, "PendingSupplierConfirmation", "AwaitingHandover", "SupplierAccept", supplierId, ActorRole.Supplier, "Acc", fromUtc, "op-1"));

            // Item 2: Accept at toUtc (excluded)
            Guid o2 = Guid.NewGuid(); Guid i2 = Guid.NewGuid();
            var order2 = PostgresTestDatabaseHelper.CreateOrderWithItem(o2, i2, supplierId, 1, toUtc.AddHours(-1));
            order2.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), o2, i2, "PendingSupplierConfirmation", "AwaitingHandover", "SupplierAccept", supplierId, ActorRole.Supplier, "Acc", toUtc, "op-2"));

            // Item 3: Reject at fromUtc (included)
            Guid o3 = Guid.NewGuid(); Guid i3 = Guid.NewGuid();
            var order3 = PostgresTestDatabaseHelper.CreateOrderWithItem(o3, i3, supplierId, 1, fromUtc.AddHours(-1));
            order3.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), o3, i3, "PendingSupplierConfirmation", "Rejected", "SupplierReject", supplierId, ActorRole.Supplier, "Rej", fromUtc, "op-3"));

            // Item 4: Reject at toUtc (excluded)
            Guid o4 = Guid.NewGuid(); Guid i4 = Guid.NewGuid();
            var order4 = PostgresTestDatabaseHelper.CreateOrderWithItem(o4, i4, supplierId, 1, toUtc.AddHours(-1));
            order4.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), o4, i4, "PendingSupplierConfirmation", "Rejected", "SupplierReject", supplierId, ActorRole.Supplier, "Rej", toUtc, "op-4"));

            // Item 5: Timeout at fromUtc (included)
            Guid o5 = Guid.NewGuid(); Guid i5 = Guid.NewGuid();
            var order5 = PostgresTestDatabaseHelper.CreateOrderWithItem(o5, i5, supplierId, 1, fromUtc.AddHours(-1));
            order5.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), o5, i5, "PendingSupplierConfirmation", "Expired", "SupplierTimeout", null, ActorRole.System, "Timeout", fromUtc, "op-5"));

            // Item 6: Timeout at toUtc (excluded)
            Guid o6 = Guid.NewGuid(); Guid i6 = Guid.NewGuid();
            var order6 = PostgresTestDatabaseHelper.CreateOrderWithItem(o6, i6, supplierId, 1, toUtc.AddHours(-1));
            order6.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), o6, i6, "PendingSupplierConfirmation", "Expired", "SupplierTimeout", null, ActorRole.System, "Timeout", toUtc, "op-6"));

            // Item 7: Cancel at fromUtc (with accept beforehand) (included)
            Guid o7 = Guid.NewGuid(); Guid i7 = Guid.NewGuid();
            var order7 = PostgresTestDatabaseHelper.CreateOrderWithItem(o7, i7, supplierId, 1, fromUtc.AddHours(-2));
            order7.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), o7, i7, "PendingSupplierConfirmation", "AwaitingHandover", "SupplierAccept", supplierId, ActorRole.Supplier, "Acc", fromUtc.AddHours(-1), "op-7a"));
            order7.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), o7, i7, "AwaitingHandover", "Cancelled", "SupplierCancel", supplierId, ActorRole.Supplier, "Can", fromUtc, "op-7b"));

            // Item 8: Cancel at toUtc (with accept beforehand outside window) (excluded)
            Guid o8 = Guid.NewGuid(); Guid i8 = Guid.NewGuid();
            var order8 = PostgresTestDatabaseHelper.CreateOrderWithItem(o8, i8, supplierId, 1, fromUtc.AddHours(-3));
            order8.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), o8, i8, "PendingSupplierConfirmation", "AwaitingHandover", "SupplierAccept", supplierId, ActorRole.Supplier, "Acc", fromUtc.AddHours(-2), "op-8a"));
            order8.AddStateTransition(new RentalStateTransition(Guid.NewGuid(), o8, i8, "AwaitingHandover", "Cancelled", "SupplierCancel", supplierId, ActorRole.Supplier, "Can", toUtc, "op-8b"));

            ctx.RentalOrders.AddRange(order1, order2, order3, order4, order5, order6, order7, order8);
            await ctx.SaveChangesAsync();

            var repo = new RentalOrderRepository(ctx);
            var stats = await repo.GetSupplierConfirmationStatisticsAsync(supplierId, fromUtc, toUtc);

            Assert.Equal(1, stats.AcceptedCount);
            Assert.Equal(1, stats.RejectedCount);
            Assert.Equal(1, stats.ConfirmationTimeoutCount);
            Assert.Equal(1, stats.CancelAfterAcceptCount);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task GetSupplierConfirmationStatisticsAsync_AcceptOutsideWindow_CancelInsideWindow_StillCountsCancel()
    {
        await EnsurePostgreSqlAvailableAsync();

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using var ctx = new RentalServiceDbContext(options);
            await ctx.Database.EnsureCreatedAsync();

            Guid supplierId = Guid.NewGuid();
            Guid orderId = Guid.NewGuid();
            Guid itemId = Guid.NewGuid();

            var fromUtc = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
            var toUtc = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

            var acceptTime = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero); // September
            var cancelTime = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero); // October

            var order = PostgresTestDatabaseHelper.CreateOrderWithItem(orderId, itemId, supplierId, 1, acceptTime.AddHours(-1));
            order.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), orderId, itemId, "PendingSupplierConfirmation", "AwaitingHandover", "SupplierAccept", supplierId, ActorRole.Supplier, "Accepted in Sep", acceptTime, "op-acc"));
            order.AddStateTransition(new RentalStateTransition(
                Guid.NewGuid(), orderId, itemId, "AwaitingHandover", "Cancelled", "SupplierCancel", supplierId, ActorRole.Supplier, "Canceled in Oct", cancelTime, "op-cancel"));

            ctx.RentalOrders.Add(order);
            await ctx.SaveChangesAsync();

            var repo = new RentalOrderRepository(ctx);
            var stats = await repo.GetSupplierConfirmationStatisticsAsync(supplierId, fromUtc, toUtc);

            Assert.Equal(0, stats.AcceptedCount);
            Assert.Equal(1, stats.CancelAfterAcceptCount);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }
}
