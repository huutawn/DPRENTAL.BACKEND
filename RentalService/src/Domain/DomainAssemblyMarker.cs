using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("RentalService.Application")]
[assembly: InternalsVisibleTo("RentalService.Api.Tests")]

namespace Dprental.RentalService.Domain;

/// <summary>
/// Đánh dấu assembly Domain phục vụ nhận diện reflection và kiểm thử.
/// </summary>
public static class DomainAssemblyMarker
{
}
