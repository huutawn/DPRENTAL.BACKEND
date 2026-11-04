using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("NotificationService.Api.Tests")]

namespace Dprental.NotificationService.Application;

/// <summary>
/// Đánh dấu assembly Application phục vụ nhận diện reflection và kiểm thử.
/// </summary>
public static class ApplicationAssemblyMarker
{
}
