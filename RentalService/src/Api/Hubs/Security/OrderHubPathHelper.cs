using Dprental.RentalService.Application.Security;
using Microsoft.AspNetCore.Http;

namespace Dprental.RentalService.Api.Hubs.Security;

/// <summary>
/// Helper nhận diện chính xác endpoint của OrderHub và negotiate, loại bỏ path traversal/prefix giả mạo.
/// </summary>
public static class OrderHubPathHelper
{
    public static readonly PathString CanonicalHubPath = new(OrderHubSecurityConstants.HubEndpoint);

    public static bool IsOrderHubPath(PathString path)
    {
        if (!path.StartsWithSegments(CanonicalHubPath, StringComparison.OrdinalIgnoreCase, out PathString remaining))
        {
            return false;
        }

        return !remaining.HasValue ||
               remaining.Equals(PathString.Empty) ||
               remaining.Equals(new PathString("/negotiate"), StringComparison.OrdinalIgnoreCase);
    }
}
