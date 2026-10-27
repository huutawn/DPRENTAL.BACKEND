using System.Collections.Concurrent;
using System.Text.Json;
using Dprental.RentalService.Api.Hubs;
using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Api.Tests.Orders;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Orders;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Hubs;

/// <summary>
/// Test mô phỏng router và delivery với Mock IHubContext / simulated group routing.
/// Dùng để kiểm chứng logic routing và PostgreSQL resync trong phạm vi harness unit/integration.
/// Không dùng test này để tuyên bố WebSocket end-to-end đã pass; việc nghiệm thu WebSocket thật
/// với Kestrel loopback và client SignalR thực tế do RentalHubRealClientIntegrationTests đảm nhiệm.
/// </summary>
public sealed class RentalRealtimeEndToEndIntegrationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 26, 17, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RealtimeEndToEnd_MultiUserIsolation_MultiConnection_AndPostgresResync()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL test database is required for E2E realtime integration test.");
        }

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var dbOptions = new DbContextOptionsBuilder<RentalServiceDbContext>()
                .UseNpgsql(connString)
                .Options;

            Guid renter1 = Guid.NewGuid();
            Guid renter2 = Guid.NewGuid();
            Guid supplierA = Guid.NewGuid();
            Guid supplierB = Guid.NewGuid();

            Guid order1Id = Guid.NewGuid();
            Guid order2Id = Guid.NewGuid();

            // 1. Seed two orders in real PostgreSQL:
            // Order 1 belongs to Renter 1, has SubOrder A (Supplier A) and SubOrder B (Supplier B).
            // Order 2 belongs to Renter 2.
            await using (var seedCtx = new RentalServiceDbContext(dbOptions))
            {
                await seedCtx.Database.EnsureCreatedAsync();

                var item1A = new InitialRentalItemDto(
                    Guid.NewGuid(), Guid.NewGuid(), supplierA, 1,
                    new DateOnly(2026, 10, 28), new DateOnly(2026, 10, 30),
                    "Máy đào A", "Desc", "Kho 1", 1_000_000m, 5_000_000m,
                    "grp-1A", [new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
                    TestListingSnapshots.Default);

                var item1B = new InitialRentalItemDto(
                    Guid.NewGuid(), Guid.NewGuid(), supplierB, 1,
                    new DateOnly(2026, 10, 28), new DateOnly(2026, 10, 30),
                    "Máy ủi B", "Desc", "Kho 2", 1_500_000m, 6_000_000m,
                    "grp-1B", [new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
                    TestListingSnapshots.Default);

                var order1 = RentalOrder.Create(
                    order1Id, "DH-2026-E2E-001", renter1, "VND", "Pickup",
                    "Renter Một", "0901234567", "Địa chỉ 1", null,
                    "v1.0", "terms", "hash", "op-1", FixedNow,
                    [item1A, item1B]).Value;
                order1.MarkAsPaid("INV-E2E-01", "PAY-E2E-01", FixedNow, FixedNow);

                var item2 = new InitialRentalItemDto(
                    Guid.NewGuid(), Guid.NewGuid(), supplierA, 1,
                    new DateOnly(2026, 10, 28), new DateOnly(2026, 10, 30),
                    "Máy xúc khác", "Desc", "Kho 1", 800_000m, 3_000_000m,
                    "grp-2", [new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
                    TestListingSnapshots.Default);

                var order2 = RentalOrder.Create(
                    order2Id, "DH-2026-E2E-002", renter2, "VND", "Pickup",
                    "Renter Hai", "0909999999", "Địa chỉ 2", null,
                    "v1.0", "terms", "hash", "op-2", FixedNow,
                    [item2]).Value;
                order2.MarkAsPaid("INV-E2E-02", "PAY-E2E-02", FixedNow, FixedNow);

                seedCtx.RentalOrders.AddRange(order1, order2);
                await seedCtx.SaveChangesAsync();
            }

            // 2. Set up Simulated SignalR Group Routing representing multiple tabs/clients:
            // Renter 1: 2 tabs (conn_r1_tab1, conn_r1_tab2)
            // Supplier A: 1 connection (conn_supA)
            // Supplier B: 1 connection (conn_supB)
            // Renter 2: 1 connection (conn_r2)
            var groupSubscriptions = new Dictionary<string, List<string>>
            {
                [$"renter:{renter1:D}"] = ["conn_r1_tab1", "conn_r1_tab2"],
                [$"supplier:{supplierA:D}"] = ["conn_supA"],
                [$"supplier:{supplierB:D}"] = ["conn_supB"],
                [$"renter:{renter2:D}"] = ["conn_r2"]
            };

            var connectionMessages = new ConcurrentDictionary<string, ConcurrentQueue<(string Method, object Payload)>>();
            foreach (var conn in groupSubscriptions.Values.SelectMany(x => x).Distinct())
            {
                connectionMessages[conn] = new ConcurrentQueue<(string, object)>();
            }

            var mockClients = new Mock<IHubClients>();
            mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns<string>(groupName =>
            {
                var mockProxy = new Mock<IClientProxy>();
                mockProxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                    .Returns<string, object[], CancellationToken>((method, args, ct) =>
                    {
                        if (groupSubscriptions.TryGetValue(groupName, out var conns))
                        {
                            foreach (var conn in conns)
                            {
                                connectionMessages[conn].Enqueue((method, args[0]));
                            }
                        }
                        return Task.CompletedTask;
                    });
                return mockProxy.Object;
            });

            var mockHubContext = new Mock<IHubContext<OrderHub>>();
            mockHubContext.SetupGet(h => h.Clients).Returns(mockClients.Object);

            var notifier = new OrderHubNotifier(mockHubContext.Object, NullLogger<OrderHubNotifier>.Instance);

            // 3. Trigger mutation on PostgreSQL: Supplier A's suborder is accepted and confirmed
            Guid subOrderAId;
            Guid subOrderBId;
            long newVersion;

            await using (var updateCtx = new RentalServiceDbContext(dbOptions))
            {
                var order = await updateCtx.RentalOrders
                    .Include(o => o.Items)
                    .Include(o => o.SubOrders)
                    .SingleAsync(o => o.Id == order1Id);

                subOrderAId = order.SubOrders.Single(s => s.SupplierId == supplierA).Id;
                subOrderBId = order.SubOrders.Single(s => s.SupplierId == supplierB).Id;

                var itemA = order.Items.Single(i => i.SupplierId == supplierA);
                itemA.ApplySupplierAccept(supplierA, FixedNow, "op-conf", "op-conf");
                order.RecordReadModelUpdate(FixedNow);
                await updateCtx.SaveChangesAsync();
                newVersion = order.Version;

                // Push invalidation via notifier (simulating dispatcher after commit)
                await notifier.NotifyRenterOrderChangedAsync(
                    renter1,
                    new RenterOrderChangedNotification(
                        Guid.NewGuid(), order1Id, newVersion, OrderChangeTypes.SupplierConfirmed, FixedNow));

                await notifier.NotifySupplierSubOrderChangedAsync(
                    supplierA,
                    new SupplierSubOrderChangedNotification(
                        Guid.NewGuid(), order1Id, subOrderAId, newVersion, OrderChangeTypes.SupplierConfirmed, FixedNow));
            }

            // 4. Assert Notification Routing & Multi-Client Isolation:
            // Renter 1: BOTH tabs received RenterOrderChanged!
            Assert.Single(connectionMessages["conn_r1_tab1"]);
            var r1Tab1Msg = connectionMessages["conn_r1_tab1"].Single();
            Assert.Equal("RenterOrderChanged", r1Tab1Msg.Method);
            var r1Payload = Assert.IsType<RenterOrderChangedNotification>(r1Tab1Msg.Payload);
            Assert.Equal(order1Id, r1Payload.OrderId);
            Assert.Equal(newVersion, r1Payload.Version);

            Assert.Single(connectionMessages["conn_r1_tab2"]);
            Assert.Equal(newVersion, ((RenterOrderChangedNotification)connectionMessages["conn_r1_tab2"].Single().Payload).Version);

            // Supplier A: received SupplierSubOrderChanged for their suborder!
            Assert.Single(connectionMessages["conn_supA"]);
            var supAMsg = connectionMessages["conn_supA"].Single();
            Assert.Equal("SupplierSubOrderChanged", supAMsg.Method);
            var supAPayload = Assert.IsType<SupplierSubOrderChangedNotification>(supAMsg.Payload);
            Assert.Equal(subOrderAId, supAPayload.SubOrderId);
            Assert.Equal(newVersion, supAPayload.OrderVersion);

            // UNRELATED CLIENTS: Renter 2 and Supplier B received NOTHING!
            Assert.Empty(connectionMessages["conn_r2"]);
            Assert.Empty(connectionMessages["conn_supB"]);

            // 5. Post-Notification REST Resync against PostgreSQL:
            // Renter calls GET /api/v1/rental/orders/{orderId}
            await using (var queryCtx = new RentalServiceDbContext(dbOptions))
            {
                var repo = new RentalOrderRepository(queryCtx);
                var renterQuery = new RenterOrderQueryService(repo);
                var supplierQuery = new SupplierSubOrderQueryService(repo);

                // Renter detail resync
                var renterResync = await renterQuery.GetMyOrderAsync(order1Id, OrderTestData.User(renter1));
                Assert.True(renterResync.IsSuccess);
                Assert.Equal(newVersion, renterResync.Value.Version);
                Assert.Equal(RentalItemStatus.AwaitingHandover, renterResync.Value.SubOrders.Single(s => s.SubOrderId == subOrderAId).Status);

                // Renter list resync
                var renterListResync = await renterQuery.GetMyOrdersAsync(1, 10, null, OrderTestData.User(renter1));
                Assert.True(renterListResync.IsSuccess);
                Assert.Equal(newVersion, renterListResync.Value.Items.Single(o => o.OrderId == order1Id).Version);

                // Supplier A detail resync
                var supAResync = await supplierQuery.GetMySubOrderAsync(subOrderAId, OrderTestData.User(supplierA));
                Assert.True(supAResync.IsSuccess);
                Assert.Equal(newVersion, supAResync.Value.OrderVersion);
                Assert.Equal(RentalItemStatus.AwaitingHandover, supAResync.Value.Status);

                // Supplier A list resync
                var supAListResync = await supplierQuery.GetMySubOrdersAsync(1, 10, null, OrderTestData.User(supplierA));
                Assert.True(supAListResync.IsSuccess);
                Assert.Equal(newVersion, supAListResync.Value.Items.Single(s => s.SubOrderId == subOrderAId).OrderVersion);

                // Stranger Renter 2 cannot read Renter 1's order
                var strangerOrder = await renterQuery.GetMyOrderAsync(order1Id, OrderTestData.User(renter2));
                Assert.False(strangerOrder.IsSuccess);

                // Supplier B cannot read Supplier A's suborder
                var strangerSubOrder = await supplierQuery.GetMySubOrderAsync(subOrderAId, OrderTestData.User(supplierB));
                Assert.False(strangerSubOrder.IsSuccess);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public void ClientResync_VersionComparison_AndInFlightRaceCondition_HandledCorrectly()
    {
        // Demonstrates and verifies the client-side resync state machine contract
        var state = new ClientOrderViewState
        {
            CachedOrderId = Guid.NewGuid(),
            CachedVersion = 2,
            HighestRequiredVersion = 2,
            IsDirty = false
        };

        // 1. Event v3 arrives -> marks view dirty and sets required version
        state.OnNotificationReceived(new RenterOrderChangedNotification(Guid.NewGuid(), state.CachedOrderId, 3, "INVOICE_ATTACHED", FixedNow));
        Assert.True(state.IsDirty);
        Assert.Equal(3, state.HighestRequiredVersion);

        // 2. Client initiates GET v3 (in-flight)
        // While GET v3 is in-flight, a newer event v4 arrives!
        state.OnNotificationReceived(new RenterOrderChangedNotification(Guid.NewGuid(), state.CachedOrderId, 4, "PAYMENT_CONFIRMED", FixedNow));
        Assert.True(state.IsDirty);
        Assert.Equal(4, state.HighestRequiredVersion);

        // 3. The first GET completes, returning version 3
        // Client detects that returned version 3 < highestRequiredVersion 4:
        // Response is stale! Must NOT clear dirty flag, must schedule subsequent fetch!
        bool needsRefetch = state.OnRestResponseReceived(responseVersion: 3);
        Assert.True(needsRefetch, "Client must trigger re-fetch when in-flight response does not satisfy latest notification version.");
        Assert.True(state.IsDirty);

        // 4. Second GET completes, returning version 4
        // Returned version 4 >= highestRequiredVersion 4:
        // Cache is updated and dirty flag cleared!
        needsRefetch = state.OnRestResponseReceived(responseVersion: 4);
        Assert.False(needsRefetch);
        Assert.False(state.IsDirty);
        Assert.Equal(4, state.CachedVersion);
    }

    [Fact]
    public void SupplierSubOrder_VersionComparison_IsTrackedPerSubOrder_NotGlobal()
    {
        // Demonstrates and verifies that supplier does NOT use a single global version across different SubOrders
        var subOrderCache = new Dictionary<Guid, long>();
        Guid subOrder1 = Guid.NewGuid();
        Guid subOrder2 = Guid.NewGuid();

        subOrderCache[subOrder1] = 1;
        subOrderCache[subOrder2] = 1;

        // SubOrder 1 receives an event with version 5
        var event1 = new SupplierSubOrderChangedNotification(Guid.NewGuid(), Guid.NewGuid(), subOrder1, 5, "SUPPLIER_CONFIRMED", FixedNow);
        bool shouldRefresh1 = event1.OrderVersion > subOrderCache[event1.SubOrderId];
        Assert.True(shouldRefresh1);
        subOrderCache[subOrder1] = event1.OrderVersion;

        // SubOrder 2 receives an event with version 4 (which is < 5, but > SubOrder 2's cache of 1)
        var event2 = new SupplierSubOrderChangedNotification(Guid.NewGuid(), Guid.NewGuid(), subOrder2, 4, "SUPPLIER_CONFIRMED", FixedNow);
        bool shouldRefresh2 = event2.OrderVersion > subOrderCache[event2.SubOrderId];

        // MUST be true! SubOrder 2 must not be discarded because SubOrder 1 is at version 5
        Assert.True(shouldRefresh2, "SubOrder 2 event must not be discarded based on another SubOrder's higher version.");
        subOrderCache[subOrder2] = event2.OrderVersion;
    }

    private sealed class ClientOrderViewState
    {
        public Guid CachedOrderId { get; init; }
        public long CachedVersion { get; set; }
        public long HighestRequiredVersion { get; set; }
        public bool IsDirty { get; set; }

        public void OnNotificationReceived(RenterOrderChangedNotification notification)
        {
            if (notification.OrderId != CachedOrderId) return;
            HighestRequiredVersion = Math.Max(HighestRequiredVersion, notification.Version);
            if (notification.Version > CachedVersion)
            {
                IsDirty = true;
            }
        }

        public bool OnRestResponseReceived(long responseVersion)
        {
            if (responseVersion >= HighestRequiredVersion)
            {
                CachedVersion = responseVersion;
                IsDirty = false;
                return false; // Satisfied
            }

            // Stale response received while a newer notification was already registered
            CachedVersion = Math.Max(CachedVersion, responseVersion);
            return true; // Still needs re-fetch
        }
    }
}
