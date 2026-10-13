using Dprental.PaymentService.Domain.Entities;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Unit;

public sealed class InvoiceDomainTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateInvoice_CalculatesTotalsIndependently_AcrossMultipleSuppliers()
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplier1 = Guid.NewGuid();
        Guid supplier2 = Guid.NewGuid();

        var item1 = InvoiceItem.Create(Guid.NewGuid(), supplier1, 500_000m, 1_000_000m).Value;
        var item2 = InvoiceItem.Create(Guid.NewGuid(), supplier2, 700_000m, 300_000m).Value;

        var invoiceRes = Invoice.Create(
            orderId,
            "ORD-001",
            renterId,
            "VND",
            FixedNow.AddMinutes(30),
            [item1, item2],
            "DPR ABC234XYZ9",
            "MBBank",
            "0383799632",
            FixedNow);

        Assert.True(invoiceRes.IsSuccess);
        var invoice = invoiceRes.Value;

        Assert.Equal(1_200_000m, invoice.TotalRentalAmount);
        Assert.Equal(1_300_000m, invoice.TotalDepositAmount);
        Assert.Equal(2_500_000m, invoice.TotalAmount);
        Assert.Equal(InvoiceStatus.Pending, invoice.Status);
        Assert.Equal(2, invoice.Items.Count);
    }

    [Fact]
    public void CreateInvoice_WithZeroTotal_InitializesAsPaid()
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid supplier = Guid.NewGuid();

        var item = InvoiceItem.Create(Guid.NewGuid(), supplier, 0m, 0m).Value;

        var invoiceRes = Invoice.Create(
            orderId,
            "ORD-FREE-001",
            renterId,
            "VND",
            FixedNow.AddMinutes(30),
            [item],
            "DPR ABC234XYZ9",
            "MBBank",
            "0383799632",
            FixedNow);

        Assert.True(invoiceRes.IsSuccess);
        var invoice = invoiceRes.Value;

        Assert.Equal(0m, invoice.TotalAmount);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(FixedNow, invoice.PaidAt);
        Assert.NotNull(invoice.PaymentRef);
        Assert.StartsWith("PAY-", invoice.PaymentRef, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateInvoice_RejectsPastDeadline_ForPositiveTotal()
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        var item = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 100_000m, 0m).Value;

        var invoiceRes = Invoice.Create(
            orderId,
            "ORD-001",
            renterId,
            "VND",
            FixedNow.AddMinutes(-5),
            [item],
            "DPR ABC234XYZ9",
            "MBBank",
            "0383799632",
            FixedNow);

        Assert.True(invoiceRes.IsFailure);
        Assert.Equal("INVALID_PAYMENT_DEADLINE", invoiceRes.Error.Code);
    }

    [Fact]
    public void CreateInvoice_RejectsDuplicateRentalItemId()
    {
        Guid sharedItemId = Guid.NewGuid();
        var item1 = InvoiceItem.Create(sharedItemId, Guid.NewGuid(), 100_000m, 0m).Value;
        var item2 = InvoiceItem.Create(sharedItemId, Guid.NewGuid(), 200_000m, 0m).Value;

        var invoiceRes = Invoice.Create(
            Guid.NewGuid(),
            "ORD-001",
            Guid.NewGuid(),
            "VND",
            FixedNow.AddMinutes(30),
            [item1, item2],
            "DPR ABC234XYZ9",
            "MBBank",
            "0383799632",
            FixedNow);

        Assert.True(invoiceRes.IsFailure);
        Assert.Equal("Payment.DuplicateItemId", invoiceRes.Error.Code);
    }

    [Fact]
    public void Lifecycle_PendingToPaid_SucceedsAndEnforcesInvariants()
    {
        var item = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 500_000m).Value;
        var invoice = Invoice.Create(
            Guid.NewGuid(), "ORD-001", Guid.NewGuid(), "VND", FixedNow.AddMinutes(30), [item],
            "DPR ABC234XYZ9", "MBBank", "0383799632", FixedNow).Value;

        var paidRes = invoice.MarkPaid("PAY-001", 1_000_000m, "VND", FixedNow.AddMinutes(10));
        Assert.True(paidRes.IsSuccess);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal("PAY-001", invoice.PaymentRef);
        Assert.Equal(FixedNow.AddMinutes(10), invoice.PaidAt);

        // Cancel on paid invoice fails
        var cancelRes = invoice.Cancel("User requested", FixedNow.AddMinutes(15));
        Assert.True(cancelRes.IsFailure);
        Assert.Equal("INVOICE_ALREADY_PAID", cancelRes.Error.Code);
    }

    [Fact]
    public void Lifecycle_CancelExpiredInvoice_AcknowledgesClosureWithoutChangingStatus()
    {
        var item = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m).Value;
        var invoice = Invoice.Create(
            Guid.NewGuid(), "ORD-001", Guid.NewGuid(), "VND", FixedNow.AddMinutes(30), [item],
            "DPR ABC234XYZ9", "MBBank", "0383799632", FixedNow).Value;

        var expireRes = invoice.Expire(FixedNow.AddMinutes(31));
        Assert.True(expireRes.IsSuccess);
        Assert.Equal(InvoiceStatus.Expired, invoice.Status);

        // Cancel expired returns success, status remains EXPIRED
        var cancelRes = invoice.Cancel("Payment expired", FixedNow.AddMinutes(35));
        Assert.True(cancelRes.IsSuccess);
        Assert.Equal(InvoiceStatus.Expired, invoice.Status);
    }

    [Fact]
    public void Lifecycle_CancelPendingInvoice_TransitionsToCancelled()
    {
        var item = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m).Value;
        var invoice = Invoice.Create(
            Guid.NewGuid(), "ORD-001", Guid.NewGuid(), "VND", FixedNow.AddMinutes(30), [item],
            "DPR ABC234XYZ9", "MBBank", "0383799632", FixedNow).Value;

        var cancelRes = invoice.Cancel("Renter cancelled order", FixedNow.AddMinutes(10));
        Assert.True(cancelRes.IsSuccess);
        Assert.Equal(InvoiceStatus.Cancelled, invoice.Status);
        Assert.Equal(FixedNow.AddMinutes(10), invoice.CancelledAt);
        Assert.Equal("Renter cancelled order", invoice.CancelReason);
    }

    [Fact]
    public void CreateAndLifecycle_NormalizesNonZeroOffsetsToUtc()
    {
        var offsetPlus7 = TimeSpan.FromHours(7);
        var createdPlus7 = new DateTimeOffset(2026, 10, 12, 17, 0, 0, offsetPlus7);
        var deadlinePlus7 = createdPlus7.AddMinutes(30);
        var paidPlus7 = createdPlus7.AddMinutes(10);

        var item = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m).Value;
        var invoice = Invoice.Create(
            Guid.NewGuid(), "ORD-OFFSET-001", Guid.NewGuid(), "VND", deadlinePlus7, [item],
            "DPR ABC234XYZ9", "MBBank", "0383799632", createdPlus7).Value;

        Assert.Equal(TimeSpan.Zero, invoice.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, invoice.PaymentDeadline.Offset);
        Assert.Equal(createdPlus7.ToUniversalTime(), invoice.CreatedAt);
        Assert.Equal(deadlinePlus7.ToUniversalTime(), invoice.PaymentDeadline);

        var paidRes = invoice.MarkPaid("PAY-OFFSET-001", 500_000m, "VND", paidPlus7);
        Assert.True(paidRes.IsSuccess);
        Assert.NotNull(invoice.PaidAt);
        Assert.Equal(TimeSpan.Zero, invoice.PaidAt.Value.Offset);
        Assert.Equal(paidPlus7.ToUniversalTime(), invoice.PaidAt.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void MarkPaid_OnPendingInvoice_AtOrAfterDeadline_FailsWithInvoiceExpired(int minutesAfterDeadline)
    {
        var deadline = FixedNow.AddMinutes(30);
        var item = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m).Value;
        var invoice = Invoice.Create(
            Guid.NewGuid(), "ORD-DEADLINE-BOUND", Guid.NewGuid(), "VND", deadline, [item],
            "DPR ABC234XYZ9", "MBBank", "0383799632", FixedNow).Value;

        var latePaidAt = deadline.AddMinutes(minutesAfterDeadline);
        var res = invoice.MarkPaid("PAY-LATE-001", 500_000m, "VND", latePaidAt);

        Assert.True(res.IsFailure);
        Assert.Equal("INVOICE_EXPIRED", res.Error.Code);
        Assert.Equal(InvoiceStatus.Pending, invoice.Status);
    }

    [Fact]
    public void MarkPaid_ReplayOnAlreadyPaidInvoice_SucceedsEvenWhenCalledAfterDeadline()
    {
        var deadline = FixedNow.AddMinutes(30);
        var item = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m).Value;
        var invoice = Invoice.Create(
            Guid.NewGuid(), "ORD-REPLAY-PAID", Guid.NewGuid(), "VND", deadline, [item],
            "DPR ABC234XYZ9", "MBBank", "0383799632", FixedNow).Value;

        var firstRes = invoice.MarkPaid("PAY-OK-001", 500_000m, "VND", FixedNow.AddMinutes(5));
        Assert.True(firstRes.IsSuccess);

        // Replay with same PaymentRef, amount, currency, even if paidAt passed in replay is after deadline
        var replayRes = invoice.MarkPaid("PAY-OK-001", 500_000m, "VND", deadline.AddMinutes(10));
        Assert.True(replayRes.IsSuccess);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
    }
}
