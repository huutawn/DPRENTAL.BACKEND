using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Application.Utils;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Unit;

public sealed class PayloadFingerprinterTests
{
    [Fact]
    public void ComputeFingerprint_IsOrderIndependent_ForItems()
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid item1 = Guid.NewGuid();
        Guid item2 = Guid.NewGuid();
        Guid supplier = Guid.NewGuid();
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(30);

        var reqA = new CreateInvoiceRequest(
            orderId, "ORD-001", renterId, "vnd", deadline,
            [
                new CreateInvoiceItemRequest(item1, supplier, 100_000m, 50_000m),
                new CreateInvoiceItemRequest(item2, supplier, 200_000m, 80_000m)
            ],
            "op-1");

        var reqB = new CreateInvoiceRequest(
            orderId, "ORD-001", renterId, "VND", deadline,
            [
                new CreateInvoiceItemRequest(item2, supplier, 200_000m, 80_000m),
                new CreateInvoiceItemRequest(item1, supplier, 100_000m, 50_000m)
            ],
            "op-1");

        string fpA = PayloadFingerprinter.ComputeFingerprint(reqA);
        string fpB = PayloadFingerprinter.ComputeFingerprint(reqB);

        Assert.Equal(fpA, fpB);
    }

    [Fact]
    public void ComputeFingerprint_ProducesDifferentHash_WhenPayloadDiffers()
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(30);

        var req1 = new CreateInvoiceRequest(
            orderId, "ORD-001", renterId, "VND", deadline,
            [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 100_000m, 0m)],
            "op-1");

        var req2 = new CreateInvoiceRequest(
            orderId, "ORD-001", renterId, "VND", deadline,
            [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 150_000m, 0m)],
            "op-1");

        Assert.NotEqual(PayloadFingerprinter.ComputeFingerprint(req1), PayloadFingerprinter.ComputeFingerprint(req2));
    }
}
