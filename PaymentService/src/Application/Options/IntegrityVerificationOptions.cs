using System.ComponentModel.DataAnnotations;

namespace Dprental.PaymentService.Application.Options;

public sealed class IntegrityVerificationOptions
{
    public const string SectionName = "IntegrityVerification";

    public Dictionary<string, string> PublicKeys { get; set; } = new(StringComparer.Ordinal);

    public string ActiveKeyId { get; set; } = "dpr-ecdsa-2026-v1";

    public int ServerBudgetMilliseconds { get; set; } = 900;
}
