using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Returns;

public sealed class ReturnDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("condition")]
    [InlineData("future")]
    [InlineData("beforeHandover")]
    [InlineData("media")]
    [InlineData("supplier")]
    [InlineData("notHandedOver")]
    public void InvalidReturn_IsAtomic(string scenario)
    {
        var item = ReturnTests.NewOrder().Items.Single();
        var assignment = item.UnitAssignments.First();
        List<ReturnUnitData> units = [new(assignment.Id, UnitCondition.Normal, null, [])];
        var when = Now.AddHours(-1);
        var supplier = item.SupplierId;
        switch (scenario)
        {
            case "empty": units.Clear(); break;
            case "duplicate": units.Add(units[0]); break;
            case "foreign": units.Add(new(Guid.NewGuid(), UnitCondition.Normal, null, [])); break;
            case "condition": units[0] = units[0] with { Condition = (UnitCondition)99 }; break;
            case "future": when = Now.AddMinutes(1); break;
            case "beforeHandover": when = Now.AddDays(-3); break;
            case "media": units[0].MediaIds.Add(Guid.Empty); break;
            case "supplier": supplier = Guid.NewGuid(); break;
            case "notHandedOver": typeof(RentalUnitAssignment).GetProperty(nameof(RentalUnitAssignment.Status))!.SetValue(assignment, RentalUnitAssignmentStatus.Assigned); break;
        }
        var result = item.RecordAssetReturns(supplier, when, null, units, Now, supplier);
        Assert.True(result.IsFailure);
        Assert.Empty(item.ReturnRecords);
        Assert.All(item.UnitAssignments, a => Assert.Null(a.ReturnRecordId));
        Assert.Null(item.DeductionDeadline);
    }

    [Fact]
    public void NormalReturn_DoesNotCompleteOrChangeRentalFee_AndCannotBeOverwritten()
    {
        var item = ReturnTests.NewOrder(1).Items.Single();
        var amount = item.RentalAmount;
        var mediaId = Guid.NewGuid();
        List<ReturnUnitData> units = [new(item.UnitAssignments.Single().Id, UnitCondition.Normal, "good", [mediaId])];
        var result = item.RecordAssetReturns(item.SupplierId, Now.AddHours(-1), null, units, Now, item.SupplierId);
        Assert.True(result.IsSuccess);
        Assert.Equal(RentalItemStatus.InRental, item.Status);
        Assert.Equal(amount, item.RentalAmount);
        Assert.Equal(0m, item.FinalDeductionAmount);
        Assert.Null(item.DeductionDeadline);
        Assert.True(item.RecordAssetReturns(item.SupplierId, Now, null, units, Now, item.SupplierId).IsFailure);
        Assert.Single(item.ReturnRecords);
        Assert.Equal("good", item.UnitAssignments.Single().ReturnNote);
        Assert.Equal(mediaId, item.UnitAssignments.Single().ReturnMediaIds.Single());
    }
}
