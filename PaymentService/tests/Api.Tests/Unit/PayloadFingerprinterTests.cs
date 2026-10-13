using Dprental.PaymentService.Common.Utils;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Unit;

public sealed class PayloadFingerprinterTests
{
    [Fact]
    public void ComputeCreateFingerprint_MatchesFixedExpectedValue_ForKnownPayload()
    {
        Guid orderId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid renterId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        DateTimeOffset deadline = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var item1 = (Guid.Parse("00000000-0000-0000-0000-000000000010"), Guid.Parse("00000000-0000-0000-0000-000000000020"), 100_000m, 50_000m);
        var item2 = (Guid.Parse("00000000-0000-0000-0000-000000000011"), Guid.Parse("00000000-0000-0000-0000-000000000020"), 200_000m, 0m);

        string hash = PayloadFingerprinter.ComputeCreateFingerprint(
            orderId, "ORD-FIXED-01", renterId, deadline, [item1, item2]);

        Assert.Equal("77515a8e2a17a22512a77b3410aea15d5e7f163a49c2ea54b510bba607d3b582", hash);
    }

    [Fact]
    public void ComputeCancelFingerprint_MatchesFixedExpectedValue_ForKnownPayload()
    {
        Guid orderId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        string invoiceRef = "INV-20261015-FIXED";
        string reason = "Customer requested cancellation";

        string hash = PayloadFingerprinter.ComputeCancelFingerprint(orderId, invoiceRef, reason);

        Assert.Equal("b003769d6f21662aff977edea5d21a42537143bb96f5719d8055add921cab074", hash);
    }

    [Fact]
    public void ComputeFingerprint_IsOrderIndependent_ForItems()
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid item1Id = Guid.NewGuid();
        Guid item2Id = Guid.NewGuid();
        Guid supplier = Guid.NewGuid();
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(30);

        var item1 = (item1Id, supplier, 100_000m, 50_000m);
        var item2 = (item2Id, supplier, 200_000m, 80_000m);

        string fpA = PayloadFingerprinter.ComputeCreateFingerprint(
            orderId, "ORD-001", renterId, deadline, [item1, item2]);
        string fpB = PayloadFingerprinter.ComputeCreateFingerprint(
            orderId, "ORD-001", renterId, deadline, [item2, item1]);

        Assert.Equal(fpA, fpB);
    }

    [Fact]
    public void ComputeFingerprint_ProducesDifferentHash_WhenPayloadDiffers()
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(30);

        var itemA = (Guid.NewGuid(), Guid.NewGuid(), 100_000m, 0m);
        var itemB = (Guid.NewGuid(), Guid.NewGuid(), 150_000m, 0m);

        string hash1 = PayloadFingerprinter.ComputeCreateFingerprint(
            orderId, "ORD-001", renterId, deadline, [itemA]);
        string hash2 = PayloadFingerprinter.ComputeCreateFingerprint(
            orderId, "ORD-001", renterId, deadline, [itemB]);

        Assert.NotEqual(hash1, hash2);
    }
}
