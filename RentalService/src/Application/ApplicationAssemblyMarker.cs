using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("RentalService.Api.Tests")]

namespace Dprental.RentalService.Application;

/// <summary>
/// Đánh dấu assembly Application phục vụ nhận diện reflection và kiểm thử.
/// </summary>
public static class ApplicationAssemblyMarker
{
}
