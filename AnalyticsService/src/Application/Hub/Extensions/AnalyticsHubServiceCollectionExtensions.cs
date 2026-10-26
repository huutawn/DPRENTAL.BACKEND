using Dprental.AnalyticsService.Application.Hub.Options;
using Dprental.AnalyticsService.Application.Hub.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dprental.AnalyticsService.Application.Hub.Extensions;

public static class AnalyticsHubServiceCollectionExtensions
{
    public static IServiceCollection AddAnalyticsHub(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AnalyticsHubOptions>()
            .Bind(configuration.GetSection(AnalyticsHubOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSignalR();

        // Cấu hình SignalR từ AnalyticsHubOptions qua Options pattern.
        // Khác PaymentHub: không gọi BuildServiceProvider() trong lúc đăng ký DI (tạo ra container thứ hai).
        services.AddOptions<HubOptions>()
            .Configure<IOptions<AnalyticsHubOptions>>((hub, accessor) =>
            {
                AnalyticsHubOptions options = accessor.Value;
                hub.EnableDetailedErrors = options.EnableDetailedErrors;
                hub.HandshakeTimeout = TimeSpan.FromSeconds(options.HandshakeTimeoutSeconds);
                hub.KeepAliveInterval = TimeSpan.FromSeconds(options.KeepAliveIntervalSeconds);
                hub.ClientTimeoutInterval = TimeSpan.FromSeconds(options.ClientTimeoutIntervalSeconds);
                hub.MaximumReceiveMessageSize = options.MaximumReceiveMessageSize;
            });

        services.AddSingleton<IUserIdProvider, AnalyticsUserIdProvider>();
        services.AddSingleton<IAuthorizationHandler, AnalyticsHubClientHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(AnalyticsHubSecurityConstants.PolicyName, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new AnalyticsHubClientRequirement()));

        services.AddSingleton<IAnalyticsHubNotifier, AnalyticsHubNotifier>();

        return services;
    }
}
