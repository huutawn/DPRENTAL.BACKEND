using System.Security.Claims;
using Dprental.PaymentService.Application.Security.Jwt;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Application.Hub.Security;

public sealed class PaymentUserIdProvider(IOptions<PaymentJwtOptions> jwtOptions) : IUserIdProvider
{
    private readonly PaymentJwtOptions _jwtOptions = jwtOptions?.Value ?? throw new ArgumentNullException(nameof(jwtOptions));

    public string? GetUserId(HubConnectionContext connection)
    {
        var principal = connection?.User;
        if (principal?.Identity is not { IsAuthenticated: true })
        {
            return null;
        }

        // Service tokens must not connect to user Hub
        string? uClaim = principal.FindFirst("u")?.Value;
        if (string.Equals(uClaim, "svc", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Validate client audience
        var audClaims = principal.FindAll("aud").Select(c => c.Value).ToList();
        if (!audClaims.Contains(_jwtOptions.ClientAudience, StringComparer.Ordinal))
        {
            return null;
        }

        // Extract sub and ensure non-empty Guid
        string? sub = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(sub, out var guid) && guid != Guid.Empty)
        {
            return guid.ToString("D");
        }

        return null;
    }
}
