using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Orders;

public sealed class RentalSubOrderTests
{
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _supplierBinh = Guid.NewGuid();
    private readonly Guid _supplierCuong = Guid.NewGuid();

    [Fact]
    public void Create_WithLinesFromTwoSuppliers_GroupsLinesIntoOneSubOrderPerSupplierInOrderOfAppearance()
    {
        InitialRentalItemDto excavator = OrderTestData.Line(_supplierBinh, "Máy xúc CAT 320", "Máy xúc", "Caterpillar", 1, 3_000_000m, 20_000_000m);
        InitialRentalItemDto mixer = OrderTestData.Line(_supplierCuong, "Máy trộn bê tông 350L", "Máy trộn", "Hòa Phát", 2, 400_000m, 3_000_000m);
        InitialRentalItemDto compactor = OrderTestData.Line(_supplierBinh, "Máy đầm cóc Mikasa", "Máy đầm", "Mikasa", 1, 300_000m, 2_000_000m);

        RentalOrder order = OrderTestData.Order(_renterId, "DH-2026-000007", OrderTestData.Now, excavator, mixer, compactor);

        Assert.Equal(2, order.SubOrders.Count);
        RentalSubOrder binh = order.SubOrders.Single(s => s.SupplierId == _supplierBinh);
        RentalSubOrder cuong = order.SubOrders.Single(s => s.SupplierId == _supplierCuong);

        Assert.Equal("DH-2026-000007-01", binh.SubOrderCode);
        Assert.Equal("DH-2026-000007-02", cuong.SubOrderCode);
        Assert.All(order.SubOrders, s => Assert.Equal(order.Id, s.RentalOrderId));
        Assert.All(order.SubOrders, s => Assert.Equal(OrderTestData.Now, s.CreatedAt));

        Assert.Equal([excavator.Id, compactor.Id], binh.Items.Select(i => i.Id));
        Assert.Equal([mixer.Id], cuong.Items.Select(i => i.Id));
        Assert.All(order.Items, i => Assert.Equal(
            order.SubOrders.Single(s => s.SupplierId == i.SupplierId).Id,
            i.RentalSubOrderId));
    }

    [Fact]
    public void Create_WithOneSupplier_CreatesASingleSubOrderHoldingEveryLine()
    {
        RentalOrder order = OrderTestData.Order(
            _renterId,
            "DH-2026-000008",
            OrderTestData.Now,
            OrderTestData.Line(_supplierBinh, "Máy xúc CAT 320", "Máy xúc", "Caterpillar", 1, 3_000_000m, 20_000_000m),
            OrderTestData.Line(_supplierBinh, "Máy đầm cóc Mikasa", "Máy đầm", "Mikasa", 2, 300_000m, 2_000_000m));

        RentalSubOrder subOrder = Assert.Single(order.SubOrders);
        Assert.Equal("DH-2026-000008-01", subOrder.SubOrderCode);
        Assert.Equal(2, subOrder.Items.Count);
    }

    [Fact]
    public void MarkAsPaid_GivesEveryLineOfASubOrderTheEarliestConfirmationDeadline()
    {
        // Thanh toán 10:00 ngày 09/10 giờ Việt Nam. Máy xúc giao 10/10 nên hạn là 23:59:59 ngày 09/10;
        // máy đầm giao 12/10 thì riêng nó được 24 giờ, nhưng cùng đơn con với máy xúc nên dùng hạn sớm hơn.
        RentalOrder order = OrderTestData.Order(
            _renterId,
            "DH-2026-000009",
            OrderTestData.Now,
            OrderTestData.Line(_supplierBinh, "Máy xúc CAT 320", "Máy xúc", "Caterpillar", 1, 3_000_000m, 20_000_000m, new DateOnly(2026, 10, 10)),
            OrderTestData.Line(_supplierBinh, "Máy đầm cóc Mikasa", "Máy đầm", "Mikasa", 1, 300_000m, 2_000_000m),
            OrderTestData.Line(_supplierCuong, "Máy trộn bê tông 350L", "Máy trộn", "Hòa Phát", 1, 400_000m, 3_000_000m));

        OrderTestData.Paid(order, OrderTestData.Now);

        DateTimeOffset endOfNinthVietnam = new(2026, 10, 9, 16, 59, 59, TimeSpan.Zero);
        Assert.All(order.Items.Where(i => i.SupplierId == _supplierBinh), i =>
        {
            Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, i.Status);
            Assert.Equal(endOfNinthVietnam, i.SupplierConfirmationDeadline);
        });
        Assert.Equal(OrderTestData.Now.AddHours(24), order.Items.Single(i => i.SupplierId == _supplierCuong).SupplierConfirmationDeadline);
    }

    [Fact]
    public void ConfirmationHoldExpiry_IsDeadlinePlusGracePeriod()
    {
        DateTimeOffset deadline = new(2026, 10, 10, 3, 0, 0, TimeSpan.Zero);

        DateTimeOffset holdExpiry = RentalItem.CalculateConfirmationHoldExpiry(deadline, new DateOnly(2026, 10, 12));

        Assert.Equal(deadline.AddMinutes(5), holdExpiry);
    }

    [Fact]
    public void ConfirmationHoldExpiry_NeverReachesMidnightOfTheStartDateInVietnam()
    {
        // Hạn xác nhận đã là 23:59:59 đêm trước ngày giao 10/10 nên không cộng thêm được.
        DateTimeOffset deadline = new(2026, 10, 9, 16, 59, 59, TimeSpan.Zero);

        DateTimeOffset holdExpiry = RentalItem.CalculateConfirmationHoldExpiry(deadline, new DateOnly(2026, 10, 10));

        Assert.Equal(deadline, holdExpiry);
    }

    [Theory]
    [InlineData(new[] { RentalItemStatus.PendingPayment, RentalItemStatus.PendingPayment }, RentalItemStatus.PendingPayment)]
    [InlineData(new[] { RentalItemStatus.AwaitingHandover, RentalItemStatus.PendingSupplierConfirmation }, RentalItemStatus.PendingSupplierConfirmation)]
    [InlineData(new[] { RentalItemStatus.Rejected, RentalItemStatus.AwaitingHandover }, RentalItemStatus.AwaitingHandover)]
    [InlineData(new[] { RentalItemStatus.Completed, RentalItemStatus.InRental }, RentalItemStatus.InRental)]
    [InlineData(new[] { RentalItemStatus.Completed, RentalItemStatus.Rejected }, RentalItemStatus.Completed)]
    [InlineData(new[] { RentalItemStatus.Rejected, RentalItemStatus.Rejected }, RentalItemStatus.Rejected)]
    [InlineData(new[] { RentalItemStatus.Rejected, RentalItemStatus.Expired }, RentalItemStatus.Expired)]
    [InlineData(new[] { RentalItemStatus.Rejected, RentalItemStatus.Cancelled }, RentalItemStatus.Cancelled)]
    public void Summarize_UsesTheEarliestUnfinishedStepThenTheFinalOutcome(RentalItemStatus[] itemStatuses, RentalItemStatus expected)
    {
        Assert.Equal(expected, SubOrderStatusPolicy.Summarize(itemStatuses));
    }

    [Fact]
    public void Summarize_WithoutItems_Throws()
    {
        Assert.Throws<ArgumentException>(() => SubOrderStatusPolicy.Summarize([]));
    }
}
