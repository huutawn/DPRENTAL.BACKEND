using System.ComponentModel.DataAnnotations;

namespace Dprental.PaymentService.Application.Hub.Options;

public sealed class PaymentHubOptions : IValidatableObject
{
    public const string SectionName = "PaymentHub";
    public const string DefaultHubPath = "/hubs/payments";

    public string HubPath { get; set; } = DefaultHubPath;

    [Range(1, 120)]
    public int HandshakeTimeoutSeconds { get; set; } = 15;

    [Range(1, 120)]
    public int KeepAliveIntervalSeconds { get; set; } = 15;

    [Range(1, 120)]
    public int ClientTimeoutIntervalSeconds { get; set; } = 30;

    [Range(1, 10)]
    public int SendTimeoutSeconds { get; set; } = 2;

    [Range(1024, 1024 * 1024)]
    public long MaximumReceiveMessageSize { get; set; } = 32 * 1024;

    public bool EnableDetailedErrors { get; set; }

    public string[] AllowedOrigins { get; set; } = [];

    [Range(1, 100_000)]
    public int MaxConnections { get; set; } = 10_000;

    [Range(1, 10_000)]
    public int MaxGroups { get; set; } = 100;

    [Range(1, 10_000)]
    public int MaxUsersPerGroup { get; set; } = 1_000;

    [Range(1, 10_000)]
    public int MaxUsersPerSend { get; set; } = 1_000;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AllowedOrigins is null || AllowedOrigins.Length == 0)
        {
            yield return new ValidationResult(
                "AllowedOrigins must contain at least one valid frontend origin.",
                [nameof(AllowedOrigins)]);
            yield break;
        }

        foreach (var origin in AllowedOrigins)
        {
            if (string.IsNullOrWhiteSpace(origin))
            {
                yield return new ValidationResult("AllowedOrigins cannot contain empty or whitespace origins.", [nameof(AllowedOrigins)]);
                continue;
            }

            if (origin.Trim() == "*")
            {
                yield return new ValidationResult("AllowedOrigins cannot contain wildcard '*'.", [nameof(AllowedOrigins)]);
                continue;
            }

            if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                yield return new ValidationResult($"Allowed origin '{origin}' is not a valid absolute HTTP/HTTPS URI.", [nameof(AllowedOrigins)]);
            }
        }
    }
}
