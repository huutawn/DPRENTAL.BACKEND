using Dprental.AnalyticsService.Application.Security;
using Dprental.AnalyticsService.Application.Security.Jwt;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Dprental.AnalyticsService.Application.Hub.Security;

/// <summary>
/// Xác định "user" của một kết nối SignalR = claim sub (Guid). Token service hoặc sai audience → null,
/// kết nối đó không nhận được tin gửi theo user. Cùng quy tắc với PaymentUserIdProvider.
/// </summary>
public sealed class AnalyticsUserIdProvider : IUserIdProvider
{
    private readonly AnalyticsJwtOptions _jwtOptions;

    public AnalyticsUserIdProvider(IOptions<AnalyticsJwtOptions> jwtOptions)
    {
        _jwtOptions = jwtOptions?.Value ?? throw new ArgumentNullException(nameof(jwtOptions));
    }

    public string? GetUserId(HubConnectionContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.User.IsHubClient(_jwtOptions.ClientAudience)
            && connection.User.TryGetSubjectUserId(out Guid userId)
                ? userId.ToString("D")
                : null;
    }
}
