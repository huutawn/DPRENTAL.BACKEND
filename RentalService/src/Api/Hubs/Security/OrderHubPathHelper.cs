using Microsoft.AspNetCore.Http;

namespace Dprental.RentalService.Api.Hubs.Security;

/// <summary>
/// Helper nhận diện chính xác endpoint của OrderHub và negotiate, loại bỏ path traversal/prefix giả mạo.
/// </summary>
public static class OrderHubPathHelper
{
    public static readonly PathString DefaultHubPath = new("/api/v1/rental/hubs/orders");

    public static bool IsOrderHubPath(PathString path, PathString? configuredPath = null)
    {
        PathString baseHubPath = configuredPath.HasValue && configuredPath.Value.HasValue
            ? configuredPath.Value
            : DefaultHubPath;

        if (!path.StartsWithSegments(baseHubPath, StringComparison.OrdinalIgnoreCase, out PathString remaining))
        {
            return false;
        }

        return !remaining.HasValue ||
               remaining.Equals(PathString.Empty) ||
               remaining.Equals(new PathString("/negotiate"), StringComparison.OrdinalIgnoreCase);
    }
}
