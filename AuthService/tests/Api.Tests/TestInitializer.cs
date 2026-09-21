using System.Runtime.CompilerServices;

namespace Dprental.AuthService.Api.Tests;

internal static class TestInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "true");
    }
}
