using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.ReviewService.Api.Security;

/// <summary>
/// Trình xử lý xác thực Bearer token giải mã JWT claims (sub, u, r) cho ReviewService.
/// </summary>
[SuppressMessage("Usage", "CA1848:Use the LoggerMessage delegates", Justification = "Authentication boundary logging.")]
public sealed class JwtAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Bearer";

    public JwtAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader) ||
            string.IsNullOrWhiteSpace(authHeader))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string authHeaderStr = authHeader.ToString().Trim();
        if (!authHeaderStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string rawToken = authHeaderStr["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return Task.FromResult(AuthenticateResult.Fail("Empty Bearer token."));
        }

        try
        {
            var claims = ParseTokenClaims(rawToken);
            if (claims == null)
            {
                return Task.FromResult(AuthenticateResult.Fail("Malformed Bearer token."));
            }

            var identity = new ClaimsIdentity(claims, SchemeName, "sub", ClaimTypes.Role);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to parse JWT token.");
            return Task.FromResult(AuthenticateResult.Fail(ex));
        }
    }

    private static List<Claim>? ParseTokenClaims(string token)
    {
        // 1. JWT có 3 phần phân cách bởi dấu chấm
        string[] parts = token.Split('.');
        if (parts.Length == 3)
        {
            string payload = parts[1];
            byte[] bytes = DecodeBase64Url(payload);
            using var doc = JsonDocument.Parse(bytes);
            return ExtractClaimsFromJson(doc.RootElement);
        }

        // 2. Token là JSON trực tiếp
        if (token.StartsWith('{') && token.EndsWith('}'))
        {
            using var doc = JsonDocument.Parse(token);
            return ExtractClaimsFromJson(doc.RootElement);
        }

        // 3. Token là UUID thuần túy
        if (Guid.TryParse(token, out var guid))
        {
            return
            [
                new Claim("sub", guid.ToString()),
                new Claim(ClaimTypes.NameIdentifier, guid.ToString())
            ];
        }

        return null;
    }

    private static byte[] DecodeBase64Url(string input)
    {
        string padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    private static List<Claim> ExtractClaimsFromJson(JsonElement root)
    {
        var claims = new List<Claim>();

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in prop.Value.EnumerateArray())
                {
                    string itemVal = item.GetString() ?? item.GetRawText().Trim('"');
                    claims.Add(new Claim(prop.Name, itemVal));

                    if (string.Equals(prop.Name, "r", StringComparison.OrdinalIgnoreCase) &&
                        (string.Equals(itemVal, "Admin", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(itemVal, "adm", StringComparison.OrdinalIgnoreCase)))
                    {
                        claims.Add(new Claim(ClaimTypes.Role, "Admin"));
                    }
                }
            }
            else
            {
                string val = prop.Value.GetString() ?? prop.Value.GetRawText().Trim('"');
                claims.Add(new Claim(prop.Name, val));

                if (string.Equals(prop.Name, "sub", StringComparison.OrdinalIgnoreCase))
                {
                    claims.Add(new Claim(ClaimTypes.NameIdentifier, val));
                }
                else if (string.Equals(prop.Name, "r", StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(val, "Admin", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(val, "adm", StringComparison.OrdinalIgnoreCase)))
                {
                    claims.Add(new Claim(ClaimTypes.Role, "Admin"));
                }
                else if (string.Equals(prop.Name, "adm", StringComparison.OrdinalIgnoreCase) &&
                         (string.Equals(val, "true", StringComparison.OrdinalIgnoreCase) || prop.Value.ValueKind == JsonValueKind.True))
                {
                    claims.Add(new Claim(ClaimTypes.Role, "Admin"));
                }
            }
        }

        return claims;
    }
}
