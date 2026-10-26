using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Api.Hubs.Extensions;

/// <summary>
/// Mở rộng IServiceCollection đăng ký SignalR OrderHub, options và policy bảo mật.
/// </summary>
public static class OrderHubServiceCollectionExtensions
{
    public static IServiceCollection AddOrderHub(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<OrderHubOptions>()
            .Bind(configuration.GetSection(OrderHubOptions.SectionName));

        services.AddSignalR();

        services.AddOptions<HubOptions<OrderHub>>()
            .Configure<IOptions<OrderHubOptions>>((signalrOptions, hubOptionsAccessor) =>
            {
                var opts = hubOptionsAccessor.Value;
                signalrOptions.EnableDetailedErrors = opts.EnableDetailedErrors;
                signalrOptions.HandshakeTimeout = TimeSpan.FromSeconds(opts.HandshakeTimeoutSeconds);
                signalrOptions.KeepAliveInterval = TimeSpan.FromSeconds(opts.KeepAliveIntervalSeconds);
                signalrOptions.ClientTimeoutInterval = TimeSpan.FromSeconds(opts.ClientTimeoutIntervalSeconds);
                signalrOptions.MaximumReceiveMessageSize = opts.MaximumReceiveMessageSize;
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(OrderHubSecurityConstants.PolicyName, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(ctx =>
                {
                    var caller = RentalCallerContext.FromClaimsPrincipal(ctx.User);
                    return caller.IsAuthenticated &&
                           !caller.IsService &&
                           caller.UserId.HasValue &&
                           caller.UserId.Value != Guid.Empty;
                });
            });

        return services;
    }
}
