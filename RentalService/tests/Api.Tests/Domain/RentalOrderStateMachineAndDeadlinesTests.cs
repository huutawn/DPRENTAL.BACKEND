using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Domain;

public sealed class RentalOrderStateMachineAndDeadlinesTests
{
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _supplierId = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    private RentalOrder CreatePendingOrder()
    {
        InitialRentalUnitAssignmentDto assignment = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        InitialRentalItemDto itemDto = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            _supplierId,
            1,
            new(2026, 10, 10),
            new(2026, 10, 15),
            "Sony FX3 Cinema Camera",
            null,
            "District 1, HCMC",
            200_000m,
            500_000m,
            "group-fx3",
            [assignment]);

        return RentalOrder.Create(
            Guid.NewGuid(),
            "ORD-DEADLINE-001",
            _renterId,
            "VND",
            "Pickup",
            "Customer",
            "0909999999",
            "District 1",
            null,
            "refund-account",
            "v1.0",
            "terms.pdf",
            "hash-terms",
            "op-create-deadline",
            _now,
            [itemDto]).Value;
    }

    [Fact]
    public void ExpirePayment_BeforeDeadline_FailsWithPaymentDeadlineNotReached()
    {
        RentalOrder order = CreatePendingOrder();
        DateTimeOffset beforeDeadline = _now.AddMinutes(15); // Deadline is +30m

        Result result = order.ExpirePayment(beforeDeadline, "op-expire-early");

        Assert.True(result.IsFailure);
        Assert.Equal("Order.PaymentDeadlineNotReached", result.Error.Code);
        Assert.Equal(RentalOrderStatus.PendingPayment, order.Status);
    }

    [Fact]
    public void ExpirePayment_AtOrAfterDeadline_Succeeds()
    {
        RentalOrder order = CreatePendingOrder();
        DateTimeOffset atDeadline = _now.AddMinutes(30);

        Result result = order.ExpirePayment(atDeadline, "op-expire-exact");

        Assert.True(result.IsSuccess);
        Assert.Equal(RentalOrderStatus.Expired, order.Status);
    }

    [Fact]
    public void SupplierAccept_RequiresValidInventoryConfirmationRef()
    {
        RentalOrder order = CreatePendingOrder();
        order.ConfirmPayment("INV-01", "PAY-01", _now, TimeSpan.FromHours(24), "op-pay");
        RentalItem item = order.Items.First();

        // Null or whitespace confirmation ref fails
        Result failResult = order.SupplierAcceptItem(item.Id, _supplierId, "   ", _now.AddHours(1), "op-acc-fail");
        Assert.True(failResult.IsFailure);
        Assert.Equal("RentalItem.InventoryConfirmationRequired", failResult.Error.Code);
        Assert.Equal(RentalItemStatus.PendingSupplierConfirmation, item.Status);

        // Valid confirmation ref succeeds
        Result successResult = order.SupplierAcceptItem(item.Id, _supplierId, "INV-CONFIRM-999", _now.AddHours(1), "op-acc-ok");
        Assert.True(successResult.IsSuccess);
        Assert.Equal(RentalItemStatus.AwaitingHandover, item.Status);
        Assert.Equal("INV-CONFIRM-999", item.InventoryConfirmationRef);
    }

    [Fact]
    public void SupplierAccept_AfterDeadline_FailsWithDeadlineExpired()
    {
        RentalOrder order = CreatePendingOrder();
        order.ConfirmPayment("INV-01", "PAY-01", _now, TimeSpan.FromHours(24), "op-pay");
        RentalItem item = order.Items.First();

        DateTimeOffset afterDeadline = _now.AddHours(25);
        Result result = order.SupplierAcceptItem(item.Id, _supplierId, "INV-CONFIRM-1", afterDeadline, "op-acc-late");

        Assert.True(result.IsFailure);
        Assert.Equal("RentalItem.SupplierDeadlineExpired", result.Error.Code);
    }

    [Fact]
    public void SupplierReject_AfterDeadline_FailsWithDeadlineExpired()
    {
        RentalOrder order = CreatePendingOrder();
        order.ConfirmPayment("INV-01", "PAY-01", _now, TimeSpan.FromHours(24), "op-pay");
        RentalItem item = order.Items.First();

        DateTimeOffset afterDeadline = _now.AddHours(25);
        Result result = order.SupplierRejectItem(item.Id, _supplierId, "Out of stock", afterDeadline, "op-rej-late");

        Assert.True(result.IsFailure);
        Assert.Equal("RentalItem.SupplierDeadlineExpired", result.Error.Code);
    }

    [Fact]
    public void SupplierTimeout_BeforeDeadline_FailsWithDeadlineNotExpired()
    {
        RentalOrder order = CreatePendingOrder();
        order.ConfirmPayment("INV-01", "PAY-01", _now, TimeSpan.FromHours(24), "op-pay");
        RentalItem item = order.Items.First();

        DateTimeOffset beforeDeadline = _now.AddHours(23);
        Result result = order.SupplierTimeoutItem(item.Id, beforeDeadline, "op-timeout-early");

        Assert.True(result.IsFailure);
        Assert.Equal("RentalItem.SupplierDeadlineNotExpired", result.Error.Code);
    }

    [Fact]
    public void SupplierTimeout_AtOrAfterDeadline_Succeeds()
    {
        RentalOrder order = CreatePendingOrder();
        order.ConfirmPayment("INV-01", "PAY-01", _now, TimeSpan.FromHours(24), "op-pay");
        RentalItem item = order.Items.First();

        DateTimeOffset afterDeadline = _now.AddHours(24);
        Result result = order.SupplierTimeoutItem(item.Id, afterDeadline, "op-timeout-on-time");

        Assert.True(result.IsSuccess);
        Assert.Equal(RentalItemStatus.Expired, item.Status);
        Assert.Equal(RentalOrderStatus.Expired, order.Status);
    }

    [Fact]
    public void CancelItemBeforeHandover_GuardsActorOwnership()
    {
        RentalOrder order = CreatePendingOrder();
        order.ConfirmPayment("INV-01", "PAY-01", _now, TimeSpan.FromHours(24), "op-pay");
        RentalItem item = order.Items.First();
        order.SupplierAcceptItem(item.Id, _supplierId, "CONF-01", _now.AddHours(1), "op-acc");

        // Stranger role/ID fails
        Guid strangerId = Guid.NewGuid();
        Result strangerRes = order.CancelItemBeforeHandover(item.Id, strangerId, ActorRole.Renter, "I want to cancel", _now.AddHours(2), "op-cancel-stranger");
        Assert.True(strangerRes.IsFailure);
        Assert.Equal("Order.CancellationForbidden", strangerRes.Error.Code);

        // Supplier cancels with own ID succeeds
        Result supplierCancelRes = order.CancelItemBeforeHandover(item.Id, _supplierId, ActorRole.Supplier, "Equipment damaged before handover", _now.AddHours(2), "op-cancel-sup");
        Assert.True(supplierCancelRes.IsSuccess);
        Assert.Equal(RentalItemStatus.Cancelled, item.Status);
        Assert.Equal(RentalOrderStatus.Cancelled, order.Status);
    }

    [Fact]
    public void ResolveDispute_GuardsAdminRole()
    {
        RentalOrder order = CreatePendingOrder();
        order.ConfirmPayment("INV-01", "PAY-01", _now, TimeSpan.FromHours(24), "op-pay");
        RentalItem item = order.Items.First();
        order.SupplierAcceptItem(item.Id, _supplierId, "CONF-01", _now.AddHours(1), "op-acc");

        RentalUnitAssignment unit = item.UnitAssignments.First();
        order.SubmitHandover(item.Id, _supplierId, _now.AddDays(1), [new(unit.Id, UnitCondition.Normal)], null, _now.AddDays(1), "op-hand");
        order.SubmitReturn(item.Id, _supplierId, _now.AddDays(5), [new(unit.Id, UnitCondition.Damaged, "Lens crack", [Guid.NewGuid()])], null, _now.AddDays(5), "op-ret");
        order.SubmitDeductionProposal(item.Id, _supplierId, [new(unit.Id, 300_000m, "Repair cost")], _now.AddDays(5).AddHours(2), "op-prop");
        order.DisputeDeductionProposal(item.Id, _renterId, "Crack was pre-existing", null, _now.AddDays(5).AddHours(4), "op-disp");

        // Non-admin resolution fails with AdminOnlyResolution
        Guid nonAdminId = Guid.NewGuid();
        Result failRes = order.ResolveDispute(item.Id, nonAdminId, ActorRole.Renter, 150_000m, "Renter resolves own dispute", null, _now.AddDays(6), "op-resolve-non-admin");
        Assert.True(failRes.IsFailure);
        Assert.Equal("Dispute.AdminOnly", failRes.Error.Code);

        // Admin resolution succeeds
        Guid adminId = Guid.NewGuid();
        Result adminRes = order.ResolveDispute(item.Id, adminId, ActorRole.Admin, 150_000m, "Split 50-50 based on evidence", null, _now.AddDays(6), "op-resolve-admin");
        Assert.True(adminRes.IsSuccess);
        Assert.Equal(150_000m, item.FinalDeductionAmount);
        Assert.Equal(DisputeStatus.Resolved, item.DeductionProposal!.Dispute!.Status);
    }

    [Fact]
    public void LatePayment_AfterExpiredOrCancelled_MaintainsTerminalStatusAndRequestsRefund()
    {
        RentalOrder order = CreatePendingOrder();

        // Expire order after 30 minutes
        order.ExpirePayment(_now.AddMinutes(31), "op-expire");
        Assert.Equal(RentalOrderStatus.Expired, order.Status);

        // InvoicePaid arrives late
        DateTimeOffset latePaymentTime = _now.AddMinutes(40);
        Result latePayResult = order.ConfirmPayment("INV-LATE", "PAY-LATE", latePaymentTime, TimeSpan.FromHours(24), "op-pay-late");

        Assert.True(latePayResult.IsSuccess);
        Assert.Equal(RentalOrderStatus.Expired, order.Status);
        Assert.True(order.LatePaymentRefundRequired);
        Assert.All(order.Items, i => Assert.Equal(RentalItemStatus.Expired, i.Status));
    }
}
