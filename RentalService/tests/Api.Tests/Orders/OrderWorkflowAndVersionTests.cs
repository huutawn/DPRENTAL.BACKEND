using System.Text.Json;
using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Orders;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Orders;

public sealed class OrderWorkflowAndVersionTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static (RentalOrder Order, RentalItem FirstItem, RentalItem SecondItem) CreateOrderWithTwoItems(Guid renterId, Guid supplierId)
    {
        Guid orderId = Guid.NewGuid();
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
            "Máy xúc mini CAT 301.5",
            "Máy xúc mini",
            "Kho Thủ Đức",
            1_000_000m,
            5_000_000m,
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
            "Máy trộn bê tông 350L",
            "Máy trộn bê tông",
            "Kho Thủ Đức",
            300_000m,
            2_000_000m,
            $"group-{item2Id:N}",
            assignments2,
            TestListingSnapshots.Default);

        var order = RentalOrder.Create(
            orderId,
            "ORD-WF-001",
            renterId,
            "VND",
            "Pickup",
            "Nguyen Van An",
            "0901234567",
            "123 Duong 1",
            null,
            "v1.0",
            "terms-doc",
            "hash-terms",
            "op-checkout",
            FixedNow,
            [item1Dto, item2Dto]).Value;

        var items = order.Items.ToList();
        return (order, items[0], items[1]);
    }

    [Fact]
    public void PartialReturn_ItemDataChanges_WhenOrderStatusRemainsInRental_VersionIncrements()
    {
        Guid renterId = Guid.NewGuid();
        Guid supplierId = Guid.NewGuid();
        var (order, item1, item2) = CreateOrderWithTwoItems(renterId, supplierId);

        order.MarkAsPaid("INV-1", "PAY-1", FixedNow, FixedNow);
        order.RecordReadModelUpdate(FixedNow);
        long vPaid = order.Version;
        Assert.Equal(2, vPaid);

        // Accept & Handover both items -> Order is InRental
        var statusProp = typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!;
        statusProp.SetValue(item1, RentalItemStatus.InRental);
        statusProp.SetValue(item2, RentalItemStatus.InRental);
        RentalOrderStatusPolicy.Apply(order, FixedNow.AddHours(1), "op-handover");
        order.RecordReadModelUpdate(FixedNow.AddHours(1));

        Assert.Equal(RentalOrderStatus.InRental, order.Status);
        long vInRental = order.Version;
        Assert.Equal(3, vInRental);

        // 1. Partial Return: Only Item 1 is returned -> Item 1 becomes PendingSettlement
        statusProp.SetValue(item1, RentalItemStatus.PendingSettlement);

        // Rollup evaluates: active items are [Item 1 (PendingSettlement), Item 2 (InRental)].
        // Condition 3: Any active item InRental -> candidate is InRental.
        // Order.Status was InRental -> candidate == order.Status -> returns false (no status change).
        bool rollupChanged = RentalOrderStatusPolicy.Apply(order, FixedNow.AddHours(2), "op-ret-1");
        Assert.False(rollupChanged);
        Assert.Equal(RentalOrderStatus.InRental, order.Status);

        // Application records read model update because Item 1 was mutated
        DateTimeOffset retTime = FixedNow.AddHours(2);
        order.RecordReadModelUpdate(retTime);
        Assert.Equal(vInRental + 1, order.Version);
        Assert.Equal(retTime, order.UpdatedAt);

        // 2. Second item returned -> Item 2 becomes PendingSettlement
        statusProp.SetValue(item2, RentalItemStatus.PendingSettlement);
        DateTimeOffset ret2Time = FixedNow.AddHours(3);

        // Rollup evaluates: active items are both PendingSettlement.
        // Condition 4: Any active item PendingSettlement -> candidate is PendingSettlement.
        // Order.Status moves from InRental to PendingSettlement!
        rollupChanged = RentalOrderStatusPolicy.Apply(order, ret2Time, "op-ret-2");
        Assert.True(rollupChanged);
        Assert.Equal(RentalOrderStatus.PendingSettlement, order.Status);

        order.RecordReadModelUpdate(ret2Time);
        Assert.Equal(vInRental + 2, order.Version);
        Assert.Equal(ret2Time, order.UpdatedAt);
    }

    [Fact]
    public void Responses_ContainVersion_AndSerializeCorrectlyToJson()
    {
        Guid renterId = Guid.NewGuid();
        Guid supplierId = Guid.NewGuid();
        var (order, item1, item2) = CreateOrderWithTwoItems(renterId, supplierId);
        order.MarkAsPaid("INV-JSON-001", "PAY-JSON-001", FixedNow, FixedNow);
        order.RecordReadModelUpdate(FixedNow.AddMinutes(5));

        Assert.Equal(2, order.Version);

        // 1. RenterOrderDetailResponse JSON has "version": 2
        var renterDetail = new RenterOrderDetailResponse(
            order.Id,
            order.OrderCode,
            order.Status,
            order.Currency,
            order.CreatedAt,
            order.PaymentDeadline,
            order.InvoiceRef,
            order.PaymentConfirmedAt,
            OrderPaymentState.Paid,
            new OrderDeliveryResponse("Pickup", "Nguyen Van An", "0901234567", "123 Duong 1", null),
            null,
            order.TotalRentalAmount,
            order.TotalDepositAmount,
            order.TotalPayableAmount,
            [],
            order.Version);

        string renterJson = JsonSerializer.Serialize(renterDetail, WebJson);
        using var renterDoc = JsonDocument.Parse(renterJson);
        Assert.True(renterDoc.RootElement.TryGetProperty("version", out JsonElement renterVersionProp));
        Assert.Equal(2, renterVersionProp.GetInt64());

        // 2. AdminOrderDetailResponse JSON has "version": 2
        var adminDetail = new AdminOrderDetailResponse(
            order.Id,
            order.OrderCode,
            order.Status,
            order.RenterId,
            order.Currency,
            order.CreatedAt,
            order.PaymentDeadline,
            order.InvoiceRef,
            order.PaymentRef,
            order.PaymentConfirmedAt,
            OrderPaymentState.Paid,
            new OrderDeliveryResponse("Pickup", "Nguyen Van An", "0901234567", "123 Duong 1", null),
            null,
            order.TotalRentalAmount,
            order.TotalDepositAmount,
            order.TotalPayableAmount,
            [],
            [],
            order.Version);

        string adminJson = JsonSerializer.Serialize(adminDetail, WebJson);
        using var adminDoc = JsonDocument.Parse(adminJson);
        Assert.True(adminDoc.RootElement.TryGetProperty("version", out JsonElement adminVersionProp));
        Assert.Equal(2, adminVersionProp.GetInt64());

        // 3. SupplierSubOrderDetailResponse JSON has "orderVersion": 2
        var supplierDetail = new SupplierSubOrderDetailResponse(
            Guid.NewGuid(),
            "SUB-001",
            order.OrderCode,
            RentalItemStatus.PendingSupplierConfirmation,
            order.Currency,
            order.PaymentConfirmedAt,
            order.CreatedAt,
            new OrderDeliveryResponse("Pickup", "Nguyen Van An", "0901234567", "123 Duong 1", null),
            order.TotalRentalAmount,
            order.TotalDepositAmount,
            order.TotalPayableAmount,
            [],
            order.Version);

        string supplierJson = JsonSerializer.Serialize(supplierDetail, WebJson);
        using var supplierDoc = JsonDocument.Parse(supplierJson);
        Assert.True(supplierDoc.RootElement.TryGetProperty("orderVersion", out JsonElement orderVersionProp));
        Assert.Equal(2, orderVersionProp.GetInt64());
    }

    [Fact]
    public async Task QueryServices_ReturnCorrectVersion_FromLiveRepository()
    {
        string dbName = Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        Guid renterId = Guid.NewGuid();
        Guid supplierId = Guid.NewGuid();
        var (order, _, _) = CreateOrderWithTwoItems(renterId, supplierId);
        order.MarkAsPaid("INV-Q-001", "PAY-Q-001", FixedNow, FixedNow);
        order.RecordReadModelUpdate(FixedNow.AddMinutes(10)); // Version = 2
        order.RecordReadModelUpdate(FixedNow.AddMinutes(20)); // Version = 3

        await using (var db = new RentalServiceDbContext(options))
        {
            db.RentalOrders.Add(order);
            await db.SaveChangesAsync();
        }

        await using (var db = new RentalServiceDbContext(options))
        {
            var repo = new RentalOrderRepository(db);

            // Renter query
            var renterService = new RenterOrderQueryService(repo);
            var renterContext = RentalCallerContext.ForUser(renterId, roles: ["renter"]);
            var renterResult = await renterService.GetMyOrderAsync(order.Id, renterContext);
            Assert.True(renterResult.IsSuccess);
            Assert.Equal(3, renterResult.Value.Version);

            // Admin query
            var adminService = new AdminOrderQueryService(repo, NullLogger<AdminOrderQueryService>.Instance);
            var adminContext = RentalCallerContext.ForUser(Guid.NewGuid(), roles: ["admin"]);
            var adminResult = await adminService.GetOrderAsync(order.Id, adminContext);
            Assert.True(adminResult.IsSuccess);
            Assert.Equal(3, adminResult.Value.Version);

            // Supplier query
            var supplierService = new SupplierSubOrderQueryService(repo);
            var supplierContext = RentalCallerContext.ForUser(supplierId, roles: ["supplier"]);
            var subOrder = order.SubOrders.First();
            var supplierResult = await supplierService.GetMySubOrderAsync(subOrder.Id, supplierContext);
            Assert.True(supplierResult.IsSuccess);
            Assert.Equal(3, supplierResult.Value.OrderVersion);
        }
    }
}
