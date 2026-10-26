using Dprental.AnalyticsService.Application.Security;
using Dprental.AnalyticsService.Application.Security.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Dprental.AnalyticsService.Application.Hub.Security;

/// <summary>
/// Chỉ cho người dùng thật (không phải token service), đúng audience client, có sub hợp lệ kết nối Hub.
/// </summary>
public sealed class AnalyticsHubClientRequirement : IAuthorizationRequirement;

/// <summary>
/// Đọc ClientAudience từ cấu hình qua DI (không đọc lại IConfiguration trong lambda như PaymentHub).
/// </summary>
public sealed class AnalyticsHubClientHandler : AuthorizationHandler<AnalyticsHubClientRequirement>
{
    private readonly AnalyticsJwtOptions _jwtOptions;

    public AnalyticsHubClientHandler(IOptions<AnalyticsJwtOptions> jwtOptions)
    {
        _jwtOptions = jwtOptions?.Value ?? throw new ArgumentNullException(nameof(jwtOptions));
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AnalyticsHubClientRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.User.IsHubClient(_jwtOptions.ClientAudience))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
