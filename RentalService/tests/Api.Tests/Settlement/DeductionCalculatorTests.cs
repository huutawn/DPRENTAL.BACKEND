using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Settlement;

public sealed class DeductionCalculatorTests
{
    // Fixture: cọc mỗi máy 500_000, 2 máy đã trả; máy thứ hai trả hỏng trừ khi secondUnitNormal.
    private static RentalItem Item(string? snapshot = null, bool secondUnitNormal = false)
    {
        var item = Returns.ReturnTests.NewOrder(2).Items.Single();
        item.SetDamageLevelsSnapshot(snapshot ?? DamageLevelTestData.SnapshotJson);
        var at = DateTimeOffset.UtcNow;
        var units = item.UnitAssignments.Select((u, i) => new ReturnUnitData(u.Id,
            i == 1 && secondUnitNormal ? UnitCondition.Normal : UnitCondition.Damaged, "Gãy răng gầu", [])).ToList();
        ReturnTestDataSeeder.SeedReturn(item, item.SupplierId, at, null, units, at, item.SupplierId);
        return item;
    }

    private static DamageAssessment A(RentalItem item, int unitIndex, int level) =>
        new(item.UnitAssignments.ElementAt(unitIndex).AssetId, level);

    [Theory]
    [InlineData(1, 50_000)]
    [InlineData(2, 150_000)]
    [InlineData(3, 250_000)]
    [InlineData(4, 500_000)]
    public void SingleUnit_AmountIsPercentOfDepositPerUnit(int level, decimal expected)
    {
        var item = Item();
        var result = DeductionCalculator.Calculate(item, [A(item, 0, level)]);

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(expected, result.Value.Total);
    }

    [Fact]
    public void MultipleUnits_SumsPerUnitAmounts_AndListsOnlyDamagedUnitsAsAffected()
    {
        var item = Item();
        var result = DeductionCalculator.Calculate(item, [A(item, 0, 2), A(item, 1, 0)]);

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(150_000m, result.Value.Total);
        Assert.Equal([item.UnitAssignments.First().AssetId], result.Value.AffectedAssetIds);
    }

    [Fact]
    public void TotalNeverExceedsItemDeposit()
    {
        var item = Item();
        var result = DeductionCalculator.Calculate(item, [A(item, 0, 4), A(item, 1, 4)]);

        Assert.Equal(item.DepositAmount, result.Value.Total);
    }

    [Fact]
    public void PercentAboveHundredIsCappedAtDepositPerUnit()
    {
        var item = Item("""[{"Level":9,"Name":"Lỗi","Percent":150}]""");
        var result = DeductionCalculator.Calculate(item, [A(item, 0, 9)]);

        Assert.Equal(item.DepositPerUnitSnapshot, result.Value.Total);
    }

    [Fact]
    public void LevelOutsideItemSnapshotIsRejected()
    {
        var item = Item();
        var result = DeductionCalculator.Calculate(item, [A(item, 0, 7)]);

        Assert.Equal("INVALID_DAMAGE_LEVEL", result.Error.Code);
    }

    [Fact]
    public void SnapshotIsUsedNotTheCurrentTable()
    {
        // Mức 1 của đơn này được chốt 20% lúc checkout; bảng seed hiện hành (10%) không ảnh hưởng.
        var item = Item("""[{"Level":1,"Name":"Nhẹ","Percent":20}]""");
        var result = DeductionCalculator.Calculate(item, [A(item, 0, 1)]);

        Assert.Equal(100_000m, result.Value.Total);
    }

    [Fact]
    public void MissingSnapshotIsRejected()
    {
        var item = Returns.ReturnTests.NewOrder(1).Items.Single();
        var result = DeductionCalculator.Calculate(item, [new DamageAssessment(Guid.NewGuid(), 1)]);

        Assert.Equal("DAMAGE_LEVELS_SNAPSHOT_MISSING", result.Error.Code);
    }

    [Fact]
    public void AllZeroPercentLevelsAreRejectedWithClearCode()
    {
        var item = Item();
        var result = DeductionCalculator.Calculate(item, [A(item, 0, 0), A(item, 1, 0)]);

        Assert.Equal("NO_DAMAGE_SELECTED", result.Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void UnitReturnedNormalIsRejected_EvenAtZeroPercent(int level)
    {
        var item = Item(secondUnitNormal: true);
        var result = DeductionCalculator.Calculate(item, [A(item, 0, 2), A(item, 1, level)]);

        Assert.Equal("ASSET_NOT_DAMAGED_IN_ITEM", result.Error.Code);
    }

    [Fact]
    public void AssetOutsideItemIsRejected()
    {
        var item = Item();
        var result = DeductionCalculator.Calculate(item, [new DamageAssessment(Guid.NewGuid(), 2)]);

        Assert.Equal("ASSET_NOT_DAMAGED_IN_ITEM", result.Error.Code);
    }

    [Fact]
    public void EmptyOrDuplicateAssessmentsAreRejected()
    {
        var item = Item();

        Assert.Equal("INVALID_DAMAGE_ASSESSMENTS", DeductionCalculator.Calculate(item, []).Error.Code);
        Assert.Equal("INVALID_DAMAGE_ASSESSMENTS",
            DeductionCalculator.Calculate(item, [A(item, 0, 1), A(item, 0, 2)]).Error.Code);
    }
}
