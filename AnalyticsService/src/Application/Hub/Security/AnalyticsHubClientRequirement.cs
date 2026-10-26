using Dprental.AnalyticsService.Application.Security;
using Dprental.AnalyticsService.Application.Security.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Dprental.AnalyticsService.Application.Hub.Security;

/// <summary>Chỉ người dùng thật (không phải service), đúng audience, có sub hợp lệ mới vào được Hub.</summary>
public sealed class AnalyticsHubClientRequirement : IAuthorizationRequirement;

/// <summary>Đọc Audience qua DI (không đọc lại IConfiguration trong lambda mỗi request).</summary>
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

        if (context.User.IsHubClient(_jwtOptions.Audience))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
