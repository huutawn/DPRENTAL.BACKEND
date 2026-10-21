using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Dprental.PaymentService.Application.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Api.Security;

public static class PaymentServiceApiKeyDefaults
{
    public const string AuthenticationScheme = "PaymentServiceApiKey";
}

public sealed class PaymentServiceApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private static readonly string[] RentalScopes =
    [
        "payment.invoice.write",
        "payment.settlement.write",
        "payment.refund.write",
        "payment.transaction.verify"
    ];

    private readonly IOptionsMonitor<RentalServiceApiKeyOptions> _apiKeyOptions;

    public PaymentServiceApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptionsMonitor<RentalServiceApiKeyOptions> apiKeyOptions)
        : base(options, logger, encoder)
    {
        _apiKeyOptions = apiKeyOptions ?? throw new ArgumentNullException(nameof(apiKeyOptions));
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Api-Key", out var values) || values.Count != 1)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string provided = values[0] ?? string.Empty;
        string configured = _apiKeyOptions.CurrentValue.RentalApiKey;
        if (string.IsNullOrWhiteSpace(provided) || string.IsNullOrWhiteSpace(configured))
        {
            return Task.FromResult(AuthenticateResult.Fail("Payment service credential is not configured."));
        }

        byte[] providedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        byte[] configuredDigest = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        if (!CryptographicOperations.FixedTimeEquals(providedDigest, configuredDigest))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid payment service credential."));
        }

        List<Claim> claims =
        [
            new("sub", "rental-service"),
            new("client_id", "rental-service"),
            new("u", "svc"),
            new(ClaimTypes.NameIdentifier, "rental-service")
        ];
        claims.AddRange(RentalScopes.Select(scope => new Claim("s", scope)));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
