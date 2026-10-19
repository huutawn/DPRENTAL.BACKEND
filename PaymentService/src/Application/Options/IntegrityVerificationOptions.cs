using System.ComponentModel.DataAnnotations;

namespace Dprental.PaymentService.Application.Options;

public sealed class IntegrityVerificationOptions
{
    public const string SectionName = "IntegrityVerification";

    public Dictionary<string, string> PublicKeys { get; set; } = new(StringComparer.Ordinal);

    public string ActiveKeyId { get; set; } = "dpr-ecdsa-2026-v1";

    public string? KeyFilePath { get; set; }

    public string Environment { get; set; } = "production";

    public string Issuer { get; set; } = "dprental-integrity-host";

    public string Audience { get; set; } = "payment-api";

    public int ServerBudgetMilliseconds { get; set; } = 900;
}
