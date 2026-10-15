using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Dprental.PaymentService.Application.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Api.Security;

public static class SePayAuthenticationDefaults
{
    public const string AuthenticationScheme = "SePayApiKey";
}

public sealed class SePayAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IOptionsMonitor<SePayOptions> _sePayOptions;
    private readonly IHostEnvironment _environment;

    public SePayAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptionsMonitor<SePayOptions> sePayOptions,
        IHostEnvironment environment)
        : base(options, logger, encoder)
    {
        _sePayOptions = sePayOptions ?? throw new ArgumentNullException(nameof(sePayOptions));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (_environment.IsProduction() && !Request.IsHttps)
        {
            return Task.FromResult(AuthenticateResult.Fail("HTTPS is required for SePay webhooks in production."));
        }

        if (!Request.Headers.TryGetValue("Authorization", out var authHeaders) || authHeaders.Count == 0)
        {
            return Task.FromResult(AuthenticateResult.Fail("Missing Authorization header."));
        }

        string authHeader = authHeaders.ToString();
        const string prefix = "Apikey ";
        if (!authHeader.StartsWith(prefix, StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid authorization scheme; expected 'Apikey <key>'."));
        }

        string providedKey = authHeader[prefix.Length..].Trim();
        string configuredKey = _sePayOptions.CurrentValue.ApiKey;

        if (string.IsNullOrEmpty(configuredKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("SePay API key is not configured."));
        }

        byte[] providedBytes = Encoding.UTF8.GetBytes(providedKey);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(configuredKey);

        if (!CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid SePay API key."));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "sepay-webhook"),
            new Claim(ClaimTypes.Role, "PaymentProvider")
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append("WWW-Authenticate", "Apikey realm=\"sepay\"");
        return Task.CompletedTask;
    }
}
