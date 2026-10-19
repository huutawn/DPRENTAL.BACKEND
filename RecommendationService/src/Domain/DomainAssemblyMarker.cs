using Dprental.RecommendationService.Common;

namespace Dprental.RecommendationService.Domain;

/// <summary>
/// Đánh dấu assembly Domain phục vụ nhận diện reflection và kiểm thử.
/// </summary>
public static class DomainAssemblyMarker
{
    internal static readonly Type CommonMarkerType = typeof(CommonAssemblyMarker);
}
