using Dprental.PaymentService.Application.Hub.Groups;
using Dprental.PaymentService.Application.Hub.Options;
using Dprental.PaymentService.Application.Hub.Security;
using Dprental.PaymentService.Application.Security.Jwt;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Application.Hub.Extensions;

public static class PaymentHubServiceCollectionExtensions
{
    public static IServiceCollection AddPaymentHub(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PaymentHubOptions>()
            .Bind(configuration.GetSection(PaymentHubOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<PaymentHubGroups>();
        services.AddSingleton<IUserIdProvider, PaymentUserIdProvider>();
        services.AddSingleton<IPaymentHubNotifier, PaymentHubNotifier>();

        var tempProvider = services.BuildServiceProvider();
        var hubOptions = tempProvider.GetService<IOptions<PaymentHubOptions>>()?.Value ?? new PaymentHubOptions();

        services.AddSignalR(options =>
        {
            options.EnableDetailedErrors = hubOptions.EnableDetailedErrors;
            options.HandshakeTimeout = TimeSpan.FromSeconds(hubOptions.HandshakeTimeoutSeconds);
            options.KeepAliveInterval = TimeSpan.FromSeconds(hubOptions.KeepAliveIntervalSeconds);
            options.ClientTimeoutInterval = TimeSpan.FromSeconds(hubOptions.ClientTimeoutIntervalSeconds);
            options.MaximumReceiveMessageSize = hubOptions.MaximumReceiveMessageSize;
        });

        services.AddAuthorizationBuilder()
            .AddPolicy(PaymentHubSecurityConstants.PolicyName, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(ctx =>
                {
                    var user = ctx.User;
                    if (user.Identity is not { IsAuthenticated: true }) return false;

                    string? uClaim = user.FindFirst("u")?.Value;
                    if (string.Equals(uClaim, "svc", StringComparison.OrdinalIgnoreCase)) return false;

                    var jwtOptions = configuration.GetSection(PaymentJwtOptions.SectionName).Get<PaymentJwtOptions>()
                                     ?? new PaymentJwtOptions();
                    var audClaims = user.FindAll("aud").Select(c => c.Value).ToList();
                    if (!audClaims.Contains(jwtOptions.ClientAudience, StringComparer.Ordinal)) return false;

                    string? sub = user.FindFirst("sub")?.Value ?? user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                    return Guid.TryParse(sub, out var guid) && guid != Guid.Empty;
                });
            });

        return services;
    }
}
