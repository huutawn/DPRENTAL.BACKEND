using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Api.Tests.Domain;

internal static class TestListingSnapshots
{
    public static RentalItemListingSnapshotDto Default { get; } = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "Máy ảnh",
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        "Sony",
        "A7 IV",
        1,
        """{"sensor":"full-frame","megapixels":33}""",
        Guid.Parse("33333333-3333-3333-3333-333333333333"));
}
