using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Domain;

public sealed class RentalOrderLifecycleAndRollupTests
{
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _supplier1 = Guid.NewGuid();
    private readonly Guid _supplier2 = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    private RentalOrder CreateTwoSupplierOrder()
    {
        InitialRentalItemDto item1 = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            _supplier1,
            1,
            new(2026, 10, 10),
            new(2026, 10, 15),
            "Camera A",
            null,
            "District 1",
            100_000m,
            200_000m,
            "hold-1",
            [new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
            TestListingSnapshots.Default);

        InitialRentalItemDto item2 = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            _supplier2,
            1,
            new(2026, 10, 12),
            new(2026, 10, 16),
            "Lens B",
            null,
            "District 3",
            50_000m,
            150_000m,
            "hold-2",
            [new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
            TestListingSnapshots.Default);

        return RentalOrder.Create(
            Guid.NewGuid(),
            "ORD-MULTI-001",
            _renterId,
            "VND",
            "Pickup",
            "Renter Name",
            "0901234567",
            "HCMC",
            null,
            "refund-ref",
            "v1.0",
            "terms-ref",
            "content-hash",
            "op-multi-01",
            _now,
            [item1, item2]).Value;
    }

    [Fact]
    public void ConfirmPayment_TransitionsOrderAndAllItems_ToPendingSupplierConfirmation()
    {
        RentalOrder order = CreateTwoSupplierOrder();
        TimeSpan supplierTimeout = TimeSpan.FromHours(24);
        DateTimeOffset paymentTime = _now.AddMinutes(5);

        Result result = order.ConfirmPayment("INV-001", "PAY-001", paymentTime, supplierTimeout, "op-pay");

        Assert.True(result.IsSuccess);
        Assert.Equal(RentalOrderStatus.PendingSupplierConfirmation, order.Status);
        Assert.Equal("INV-001", order.InvoiceRef);
        Assert.Equal("PAY-001", order.PaymentRef);
        Assert.Equal(paymentTime, order.PaymentConfirmedAt);

        Assert.All(order.Items, item =>
        {
            Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, item.Status);
            Assert.Equal(paymentTime.Add(supplierTimeout), item.SupplierConfirmationDeadline);
        });
    }

    [Fact]
    public void ExpirePayment_WhenDeadlinePassed_MarksAllItemsAndOrderExpired()
    {
        RentalOrder order = CreateTwoSupplierOrder();
        DateTimeOffset lateTime = _now.AddMinutes(31);

        Result result = order.ExpirePayment(lateTime, "op-expire");

        Assert.True(result.IsSuccess);
        Assert.Equal(RentalOrderStatus.Expired, order.Status);
        Assert.All(order.Items, item =>
        {
            Assert.Equal(RentalItemStatus.Expired, item.Status);
            Assert.All(item.UnitAssignments, u => Assert.Equal(RentalUnitAssignmentStatus.Released, u.Status));
        });
    }

    [Fact]
    public void CancelByRenter_BeforePayment_CancelsOrderAndItems()
    {
        RentalOrder order = CreateTwoSupplierOrder();

        Result wrongRenter = order.CancelByRenter(Guid.NewGuid(), "Changed mind", _now, "op-cancel");
        Assert.True(wrongRenter.IsFailure);

        Result validCancel = order.CancelByRenter(_renterId, "Changed mind", _now, "op-cancel");
        Assert.True(validCancel.IsSuccess);
        Assert.Equal(RentalOrderStatus.Cancelled, order.Status);
        Assert.All(order.Items, item => Assert.Equal(RentalItemStatus.Cancelled, item.Status));
    }

    [Fact]
    public void MultiSupplier_IndependentDecisions_DoesNotAffectOtherItem()
    {
        RentalOrder order = CreateTwoSupplierOrder();
        order.ConfirmPayment("INV-001", "PAY-001", _now, TimeSpan.FromHours(24), "op-pay");

        RentalItem item1 = order.Items.First(i => i.SupplierId == _supplier1);
        RentalItem item2 = order.Items.First(i => i.SupplierId == _supplier2);

        // Supplier 2 cannot accept or reject item 1 (SupplierMismatch)
        Result unauthorized = order.SupplierAcceptItem(item1.Id, _supplier2, _now.AddHours(1), "op-acc1");
        Assert.True(unauthorized.IsFailure);
        Assert.Equal("RentalItem.SupplierMismatch", unauthorized.Error.Code);

        // Supplier 1 accepts item 1 -> item 1 moves to AwaitingHandover
        Result accResult = order.SupplierAcceptItem(item1.Id, _supplier1, _now.AddHours(1), "op-acc1");
        Assert.True(accResult.IsSuccess);
        Assert.Equal(RentalItemStatus.AwaitingHandover, item1.Status);
        // Item 2 remains PendingSupplierConfirmation
        Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, item2.Status);
        // Order status reflects active priority: AwaitingHandover > PendingSupplierConfirmation
        Assert.Equal(RentalOrderStatus.AwaitingHandover, order.Status);

        // Supplier 2 rejects item 2 -> item 2 moves to Rejected and releases units
        Result rejResult = order.SupplierRejectItem(item2.Id, _supplier2, "Out of stock", _now.AddHours(2), "op-rej2");
        Assert.True(rejResult.IsSuccess);
        Assert.Equal(RentalItemStatus.Rejected, item2.Status);
        Assert.All(item2.UnitAssignments, u => Assert.Equal(RentalUnitAssignmentStatus.Released, u.Status));

        // Item 1 is totally unaffected!
        Assert.Equal(RentalItemStatus.AwaitingHandover, item1.Status);
        // Order status still AwaitingHandover because item 1 is AwaitingHandover
        Assert.Equal(RentalOrderStatus.AwaitingHandover, order.Status);
    }

    [Fact]
    public void SupplierDecision_AfterDeadline_FailsWithDeadlineExpired()
    {
        RentalOrder order = CreateTwoSupplierOrder();
        order.ConfirmPayment("INV-001", "PAY-001", _now, TimeSpan.FromHours(24), "op-pay");

        RentalItem item1 = order.Items.First(i => i.SupplierId == _supplier1);
        DateTimeOffset lateTime = _now.AddHours(25); // 1 hour past 24h deadline

        Result result = order.SupplierAcceptItem(item1.Id, _supplier1, lateTime, "op-acc-late");
        Assert.True(result.IsFailure);
        Assert.Equal("RentalItem.SupplierDeadlineExpired", result.Error.Code);
    }
}
