using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Dprental.Gateway.Authentication;

/// <summary>Route opt-in cho service API key. Dấu * khớp đúng một path segment.</summary>
public sealed class ApiKeyRoutePolicy
{
    private readonly (string Method, string[] Segments)[] routes;

    public ApiKeyRoutePolicy(IConfiguration configuration)
    {
        string[] entries = configuration.GetSection("Gateway:Authentication:ApiKey:RequiredRoutes")
            .Get<string[]>() ?? [];
        if (entries.Length == 0)
            throw new InvalidOperationException("Gateway:Authentication:ApiKey:RequiredRoutes phải có ít nhất một route.");

        routes = entries.Select(Parse).ToArray();
    }

    public bool RequiresApiKey(HttpRequest request)
    {
        string[] path = (request.Path.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return routes.Any(route => string.Equals(route.Method, request.Method, StringComparison.OrdinalIgnoreCase) &&
            route.Segments.Length == path.Length &&
            route.Segments.Zip(path).All(pair => pair.First == "*" ||
                string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase)));
    }

    private static (string Method, string[] Segments) Parse(string entry)
    {
        string[] parts = entry.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !parts[1].StartsWith('/') || parts[1].Contains("//", StringComparison.Ordinal) ||
            parts[0].ToUpperInvariant() is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE"))
            throw new InvalidOperationException($"Route API key không hợp lệ: {entry}");
        string[] segments = parts[1].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment.Contains('*') && segment != "*"))
            throw new InvalidOperationException($"Route API key không hợp lệ: {entry}");
        return (parts[0].ToUpperInvariant(), segments);
    }
}
