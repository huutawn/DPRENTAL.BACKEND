using Dprental.ReviewService.Common.Enums;
using Xunit;

namespace Dprental.ReviewService.Api.Tests;

public sealed class DomainEnumsTests
{
    [Fact]
    public void ReviewDirection_ContainsRequiredValues()
    {
        Assert.Equal(1, (int)ReviewDirection.renter_to_supplier_listing);
        Assert.Equal(2, (int)ReviewDirection.supplier_to_renter);
        Assert.Equal(ReviewDirection.renter_to_supplier_listing, ReviewDirection.RenterToSupplierListing);
        Assert.Equal(ReviewDirection.supplier_to_renter, ReviewDirection.SupplierToRenter);

        Assert.True(Enum.TryParse<ReviewDirection>("renter_to_supplier_listing", out var renterToSupplier));
        Assert.Equal(ReviewDirection.renter_to_supplier_listing, renterToSupplier);

        Assert.True(Enum.TryParse<ReviewDirection>("supplier_to_renter", out var supplierToRenter));
        Assert.Equal(ReviewDirection.supplier_to_renter, supplierToRenter);
    }

    [Fact]
    public void AuthorState_ContainsRequiredValues()
    {
        Assert.Equal(1, (int)AuthorState.Active);
        Assert.Equal(2, (int)AuthorState.Withdrawn);

        Assert.True(Enum.TryParse<AuthorState>("Active", out var active));
        Assert.Equal(AuthorState.Active, active);

        Assert.True(Enum.TryParse<AuthorState>("Withdrawn", out var withdrawn));
        Assert.Equal(AuthorState.Withdrawn, withdrawn);
    }

    [Fact]
    public void ModerationState_ContainsRequiredValues()
    {
        Assert.Equal(1, (int)ModerationState.Visible);
        Assert.Equal(2, (int)ModerationState.Hidden);

        Assert.True(Enum.TryParse<ModerationState>("Visible", out var visible));
        Assert.Equal(ModerationState.Visible, visible);

        Assert.True(Enum.TryParse<ModerationState>("Hidden", out var hidden));
        Assert.Equal(ModerationState.Hidden, hidden);
    }

    [Fact]
    public void ReviewAuditAction_ContainsRequiredValues()
    {
        Assert.Equal(1, (int)ReviewAuditAction.Created);
        Assert.Equal(2, (int)ReviewAuditAction.Updated);
        Assert.Equal(3, (int)ReviewAuditAction.Withdrawn);
        Assert.Equal(4, (int)ReviewAuditAction.Hidden);
        Assert.Equal(5, (int)ReviewAuditAction.Restored);

        Assert.True(Enum.TryParse<ReviewAuditAction>("Created", out _));
        Assert.True(Enum.TryParse<ReviewAuditAction>("Updated", out _));
        Assert.True(Enum.TryParse<ReviewAuditAction>("Withdrawn", out _));
        Assert.True(Enum.TryParse<ReviewAuditAction>("Hidden", out _));
        Assert.True(Enum.TryParse<ReviewAuditAction>("Restored", out _));
    }
}
