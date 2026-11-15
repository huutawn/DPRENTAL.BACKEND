using Dprental.PaymentService.Common.Utils;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Unit;

public class HareNiemeyerFeeAllocatorTests
{
    [Fact]
    public void Allocate_ZeroOrNegativeFee_ReturnsAllZeros()
    {
        var shares1 = HareNiemeyerFeeAllocator.Allocate([1_000_000m, 2_000_000m], 0m);
        Assert.Equal([0m, 0m], shares1);

        var shares2 = HareNiemeyerFeeAllocator.Allocate([1_000_000m, 2_000_000m], -100m);
        Assert.Equal([0m, 0m], shares2);
    }

    [Fact]
    public void Allocate_EmptyList_ReturnsEmpty()
    {
        var shares = HareNiemeyerFeeAllocator.Allocate(Array.Empty<decimal>(), 100_000m);
        Assert.Empty(shares);
    }

    [Fact]
    public void Allocate_ZeroTotalRental_DistributesEvenlyWithRemainder()
    {
        // 3 items with 0 rental, totalFee = 100,000 VND
        // 100,000 / 3 = 33,333 floor, remainder 1
        // shares: 33,334, 33,333, 33,333 (sum = 100,000)
        var shares = HareNiemeyerFeeAllocator.Allocate([0m, 0m, 0m], 100_000m);
        Assert.Equal(3, shares.Length);
        Assert.Equal(33_334m, shares[0]);
        Assert.Equal(33_333m, shares[1]);
        Assert.Equal(33_333m, shares[2]);
        Assert.Equal(100_000m, shares.Sum());
    }

    [Fact]
    public void Allocate_ProportionalRental_AllocatesLargestRemainderExact()
    {
        // 3 items with rentals 1,000,000, 2,000,000, 3,000,000 (total = 6,000,000)
        // totalFee = 100,000 VND
        // Exact shares:
        // Item 0: 1/6 * 100,000 = 16,666.666... -> floor 16,666, fraction 0.666...
        // Item 1: 2/6 * 100,000 = 33,333.333... -> floor 33,333, fraction 0.333...
        // Item 2: 3/6 * 100,000 = 50,000.000... -> floor 50,000, fraction 0.000...
        // Allocated sum = 99,999. Remainder = 1.
        // Highest fraction is Item 0 (0.666...), so Item 0 gets +1.
        // Final shares: 16,667, 33,333, 50,000
        var shares = HareNiemeyerFeeAllocator.Allocate([1_000_000m, 2_000_000m, 3_000_000m], 100_000m);
        Assert.Equal(16_667m, shares[0]);
        Assert.Equal(33_333m, shares[1]);
        Assert.Equal(50_000m, shares[2]);
        Assert.Equal(100_000m, shares.Sum());
    }

    [Fact]
    public void Allocate_OddVndAmount_ZeroRemainderLeakage()
    {
        // Odd fee: 35,001 VND with 3 equal items (rentals 1,000,000 each)
        // 35,001 / 3 = 11,667 exactly
        var shares = HareNiemeyerFeeAllocator.Allocate([1_000_000m, 1_000_000m, 1_000_000m], 35_001m);
        Assert.Equal(11_667m, shares[0]);
        Assert.Equal(11_667m, shares[1]);
        Assert.Equal(11_667m, shares[2]);
        Assert.Equal(35_001m, shares.Sum());
    }

    [Fact]
    public void Allocate_EqualFractions_ResolvesTiesStably()
    {
        // 2 equal items (rentals 500,000 and 500,000), totalFee = 35,001 VND
        // Exact: 17,500.5 each. Floor: 17,500 each. Remainder = 1.
        // Both have fraction 0.5. Tie-breaker by index ensures item 0 gets the +1.
        var shares = HareNiemeyerFeeAllocator.Allocate([500_000m, 500_000m], 35_001m);
        Assert.Equal(17_501m, shares[0]);
        Assert.Equal(17_500m, shares[1]);
        Assert.Equal(35_001m, shares.Sum());
    }

    [Fact]
    public void Allocate_NamedLines_ReturnsDictionaryWithCorrectIds()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var lines = new[]
        {
            new HareNiemeyerFeeAllocator.AllocatableLine(id1, 1_000_000m),
            new HareNiemeyerFeeAllocator.AllocatableLine(id2, 3_000_000m)
        };

        var dict = HareNiemeyerFeeAllocator.Allocate(lines, 40_000m);
        Assert.Equal(10_000m, dict[id1]);
        Assert.Equal(30_000m, dict[id2]);
        Assert.Equal(40_000m, dict.Values.Sum());
    }
}
