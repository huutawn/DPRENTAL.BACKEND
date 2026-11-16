using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Policies;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Policies;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test method naming convention")]
public sealed class UnapprovedInventoryLimitPolicyTests
{
    private static readonly UnapprovedInventoryLimitsOptions Defaults = new();

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void Applies_OnlyForNonAdminWithoutOrganization(bool isAdmin, bool hasOrganization, bool expected)
    {
        Guid? organizationId = hasOrganization ? Guid.NewGuid() : null;

        Assert.Equal(expected, UnapprovedInventoryLimitPolicy.Applies(isAdmin, organizationId));
    }

    [Theory]
    [InlineData(4, 4, 1, true)]
    [InlineData(5, 5, 1, false)]
    public void CheckListings_AppliesRegularLimit(int counted, int all, int adding, bool allowed)
    {
        var result = UnapprovedInventoryLimitPolicy.CheckListings(counted, all, adding, Defaults);

        Assert.Equal(allowed, result.IsSuccess);
        if (!allowed)
        {
            Assert.Equal(UnapprovedInventoryLimitPolicy.ListingLimitCode, result.Error.Code);
            Assert.Equal(ErrorType.Conflict, result.Error.Type);
        }
    }

    [Fact]
    public void CheckListings_InactiveListingsDoNotCountRegularLimitButCountHardCap()
    {
        // 5 listing Inactive: counted = 0 nhưng all = 10 -> chạm trần tuyệt đối.
        Assert.True(UnapprovedInventoryLimitPolicy.CheckListings(0, 9, 1, Defaults).IsSuccess);

        var result = UnapprovedInventoryLimitPolicy.CheckListings(0, 10, 1, Defaults);

        Assert.True(result.IsFailure);
        Assert.Equal(UnapprovedInventoryLimitPolicy.ListingLimitCode, result.Error.Code);
    }

    [Theory]
    [InlineData(29, 29, 1, true)]
    [InlineData(30, 30, 1, false)]
    [InlineData(25, 25, 6, false)]
    [InlineData(0, 0, 31, false)]
    public void CheckAssets_AppliesRegularLimit_AndRejectsWholeBatch(int counted, int all, int adding, bool allowed)
    {
        var result = UnapprovedInventoryLimitPolicy.CheckAssets(counted, all, adding, Defaults);

        Assert.Equal(allowed, result.IsSuccess);
        if (!allowed)
        {
            Assert.Equal(UnapprovedInventoryLimitPolicy.AssetLimitCode, result.Error.Code);
        }
    }

    [Fact]
    public void CheckAssets_DisabledAssetsDoNotCountRegularLimitButCountHardCap()
    {
        // Tắt nhận đặt không mở thêm chỗ vượt trần 60 máy.
        Assert.True(UnapprovedInventoryLimitPolicy.CheckAssets(0, 59, 1, Defaults).IsSuccess);

        var result = UnapprovedInventoryLimitPolicy.CheckAssets(0, 60, 1, Defaults);

        Assert.True(result.IsFailure);
        Assert.Equal(UnapprovedInventoryLimitPolicy.AssetLimitCode, result.Error.Code);
    }

    [Fact]
    public void Options_ValidOnlyWhenPositiveAndHardCapAtLeastRegular()
    {
        Assert.True(Defaults.IsValid());
        Assert.False(new UnapprovedInventoryLimitsOptions { MaxAssets = 0 }.IsValid());
        Assert.False(new UnapprovedInventoryLimitsOptions { MaxAssets = 30, MaxAssetsHardCap = 20 }.IsValid());
        Assert.False(new UnapprovedInventoryLimitsOptions { MaxListings = 5, MaxListingsHardCap = 4 }.IsValid());
    }
}
