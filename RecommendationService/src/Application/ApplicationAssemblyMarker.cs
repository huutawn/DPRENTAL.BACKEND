using Dprental.RecommendationService.Domain;

namespace Dprental.RecommendationService.Application;

/// <summary>
/// Đánh dấu assembly Application phục vụ nhận diện reflection và kiểm thử.
/// </summary>
public static class ApplicationAssemblyMarker
{
    internal static readonly Type DomainMarkerType = typeof(DomainAssemblyMarker);
}
