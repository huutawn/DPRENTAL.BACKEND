namespace Dprental.PaymentService.IntegrityHost.Options;

public sealed class IntegrityHostOptions
{
    public const string SectionName = "IntegrityHost";

    public string SigningKeyId { get; set; } = "dpr-ecdsa-2026-v1";

    public string? PrivateKeyFilePath { get; set; }

    public string? PrivateKeyBase64 { get; set; }

    public string Environment { get; set; } = "production";

    public string Issuer { get; set; } = "dprental-integrity-host";

    public string Audience { get; set; } = "payment-api";

    public Dictionary<string, string> OldPublicKeys { get; set; } = new(StringComparer.Ordinal);
    public int ConfirmationIntervalMs { get; set; } = 250;

    public int BatchSize { get; set; } = 100;

    public int PeriodicScanIntervalSeconds { get; set; } = 60;

    public string? PaymentConnectionString { get; set; }

    public string? IntegrityConnectionString { get; set; }
}
