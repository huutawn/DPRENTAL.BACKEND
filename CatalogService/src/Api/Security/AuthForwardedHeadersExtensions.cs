using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Dprental.CatalogService.Api.Security;

/// <summary>Trusts forwarded request metadata only from configured internal proxy networks.</summary>
internal static class AuthForwardedHeadersExtensions
{
    private const string SectionName = "Auth:ForwardedHeaders";

    internal static IServiceCollection AddAuthForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        AuthForwardedHeadersOptions settings = configuration.GetSection(SectionName).Get<AuthForwardedHeadersOptions>() ?? new();
        services.AddOptions<AuthForwardedHeadersOptions>()
            .Bind(configuration.GetSection(SectionName))
            .Validate(
                options => !options.Enabled || options.KnownNetworks.Length > 0,
                "Auth:ForwardedHeaders:KnownNetworks phải được cấu hình khi forwarded headers được bật.")
            .Validate(
                options => !options.Enabled || options.KnownNetworks.All(IsValidNetwork),
                "Auth:ForwardedHeaders:KnownNetworks phải chứa CIDR hợp lệ.")
            .ValidateOnStart();

        if (!settings.Enabled)
        {
            return services;
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                                       ForwardedHeaders.XForwardedProto |
                                       ForwardedHeaders.XForwardedHost;
            options.ForwardLimit = settings.ForwardLimit;

            foreach (string network in settings.KnownNetworks)
            {
                string[] parts = network.Split('/', 2);
                IPAddress address = IPAddress.Parse(parts[0]);
                int prefixLength = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
#pragma warning disable ASPDEPR005
                options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(address, prefixLength));
#pragma warning restore ASPDEPR005
            }
        });

        return services;
    }

    private static bool IsValidNetwork(string network)
    {
        string[] parts = network.Split('/', 2);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out IPAddress? address)
            || !int.TryParse(parts[1], out int prefixLength))
        {
            return false;
        }

        int maximumPrefixLength = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        return prefixLength is >= 0 && prefixLength <= maximumPrefixLength;
    }
}

/// <summary>Internal proxy networks trusted to supply original host and scheme.</summary>
internal sealed class AuthForwardedHeadersOptions
{
    public bool Enabled { get; init; }

    public int? ForwardLimit { get; init; } = 1;

    public string[] KnownNetworks { get; init; } = [];
}
