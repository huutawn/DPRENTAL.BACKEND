using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Domain;

public sealed class RentalOrderInvoiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 3, 0, 0, TimeSpan.Zero);

    private static RentalOrder PendingOrder() => CheckoutTestOrders.CreatePending(Guid.NewGuid(), "op-1", Now);

    [Fact]
    public void AttachInvoice_StoresInvoiceRef_AndKeepsOrderPendingPayment()
    {
        RentalOrder order = PendingOrder();
        long versionBefore = order.Version;
        DateTimeOffset attachedAt = Now.AddSeconds(2);

        Result result = order.AttachInvoice("  INV-001  ", attachedAt);

        Assert.True(result.IsSuccess);
        Assert.Equal("INV-001", order.InvoiceRef);
        Assert.Equal(RentalOrderStatus.PendingPayment, order.Status);
        Assert.Equal(attachedAt, order.UpdatedAt);
        Assert.Equal(versionBefore + 1, order.Version);
    }

    [Fact]
    public void AttachInvoice_IsIdempotent_ForTheSameInvoice()
    {
        RentalOrder order = PendingOrder();
        order.AttachInvoice("INV-001", Now);
        long versionAfterFirst = order.Version;

        Result result = order.AttachInvoice("inv-001", Now.AddMinutes(1));

        Assert.True(result.IsSuccess);
        Assert.Equal("INV-001", order.InvoiceRef);
        Assert.Equal(versionAfterFirst, order.Version);
        Assert.Equal(Now, order.UpdatedAt);
    }

    [Fact]
    public void AttachInvoice_RejectsADifferentInvoice()
    {
        RentalOrder order = PendingOrder();
        order.AttachInvoice("INV-001", Now);

        Result result = order.AttachInvoice("INV-002", Now);

        Assert.Equal("Order.InvoiceAlreadyAttached", result.Error.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Equal("INV-001", order.InvoiceRef);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AttachInvoice_RejectsBlankInvoiceRef(string invoiceRef)
    {
        RentalOrder order = PendingOrder();

        Result result = order.AttachInvoice(invoiceRef, Now);

        Assert.Equal("Order.InvalidInvoiceRef", result.Error.Code);
        Assert.Null(order.InvoiceRef);
    }

    [Fact]
    public void AttachInvoice_RejectsInvoiceRefLongerThanColumn()
    {
        RentalOrder order = PendingOrder();

        Result result = order.AttachInvoice(new string('x', 257), Now);

        Assert.Equal("Order.InvalidInvoiceRef", result.Error.Code);
        Assert.Null(order.InvoiceRef);
    }

    [Fact]
    public void AttachInvoice_RejectsOrderThatIsNoLongerPendingPayment()
    {
        RentalOrder order = PendingOrder();
        order.MarkPaymentExpired(Now.AddMinutes(30), "expiry-op");

        Result result = order.AttachInvoice("INV-001", Now.AddMinutes(31));

        Assert.Equal("Order.NotPendingPayment", result.Error.Code);
        Assert.Null(order.InvoiceRef);
    }
}
