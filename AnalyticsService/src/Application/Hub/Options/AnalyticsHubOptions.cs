using System.ComponentModel.DataAnnotations;

namespace Dprental.AnalyticsService.Application.Hub.Options;

/// <summary>Cấu hình SignalR Hub (section "AnalyticsHub"), cùng bộ thông số với PaymentHubOptions.</summary>
public sealed class AnalyticsHubOptions : IValidatableObject
{
    public const string SectionName = "AnalyticsHub";
    public const string DefaultHubPath = "/hubs/analytics";

    [Required]
    public string HubPath { get; set; } = DefaultHubPath;

    [Range(1, 120)]
    public int HandshakeTimeoutSeconds { get; set; } = 15;

    [Range(1, 120)]
    public int KeepAliveIntervalSeconds { get; set; } = 15;

    [Range(1, 120)]
    public int ClientTimeoutIntervalSeconds { get; set; } = 30;

    // Gửi chậm quá thì bỏ: số liệu đã lưu, FE tải lại sau vẫn thấy
    [Range(1, 10)]
    public int SendTimeoutSeconds { get; set; } = 2;

    // Client chỉ nhận tin, không gửi gì lên; giới hạn nhỏ để chặn lạm dụng
    [Range(1024, 1024 * 1024)]
    public long MaximumReceiveMessageSize { get; set; } = 32 * 1024;

    public bool EnableDetailedErrors { get; set; }

    /// <summary>Domain FE được phép kết nối. Bắt buộc chứa ít nhất một origin hợp lệ.</summary>
    public string[] AllowedOrigins { get; set; } = [];

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
