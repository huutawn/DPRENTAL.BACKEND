using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dprental.InventoryService.Domain.Clients.Media;

/// <summary>
/// Cấu hình kết nối và bảo mật tới MediaService.
/// </summary>
public sealed class MediaServiceOptions : IValidatableObject
{
    public const string SectionName = "MediaService";

    /// <summary>
    /// Địa chỉ nội bộ server-to-server tới MediaService (ví dụ: http://media-service:8080).
    /// </summary>
    [Required(ErrorMessage = "InternalBaseUrl của MediaService không được để trống.")]
    public string InternalBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Địa chỉ Gateway công khai mà browser truy cập được (ví dụ: https://gateway.example hoặc http://localhost:5050).
    /// </summary>
    [Required(ErrorMessage = "PublicGatewayUrl của MediaService không được để trống.")]
    public string PublicGatewayUrl { get; set; } = string.Empty;

    /// <summary>
    /// Khóa API key dùng chung cho các API nội bộ của MediaService.
    /// </summary>
    [Required(ErrorMessage = "ApiKey của MediaService không được để trống.")]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Thời gian chờ gọi HTTP (mặc định: 10 giây).
    /// </summary>
    [Range(1, 300, ErrorMessage = "TimeoutSeconds phải lớn hơn 0.")]
    public int TimeoutSeconds { get; set; } = 10;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // 1. Kiểm tra InternalBaseUrl
        if (string.IsNullOrWhiteSpace(InternalBaseUrl) ||
            !Uri.TryCreate(InternalBaseUrl, UriKind.Absolute, out var internalUri) ||
            (internalUri.Scheme != Uri.UriSchemeHttp && internalUri.Scheme != Uri.UriSchemeHttps))
        {
            yield return new ValidationResult(
                "InternalBaseUrl của MediaService phải là URL tuyệt đối hợp lệ với scheme http hoặc https.",
                [nameof(InternalBaseUrl)]);
        }
        else
        {
            if (!string.IsNullOrEmpty(internalUri.UserInfo) ||
                !string.IsNullOrEmpty(internalUri.Query) ||
                !string.IsNullOrEmpty(internalUri.Fragment))
            {
                yield return new ValidationResult(
                    "InternalBaseUrl của MediaService không được chứa credentials, query string hoặc fragment.",
                    [nameof(InternalBaseUrl)]);
            }
        }

        // 2. Kiểm tra PublicGatewayUrl
        if (string.IsNullOrWhiteSpace(PublicGatewayUrl) ||
            !Uri.TryCreate(PublicGatewayUrl, UriKind.Absolute, out var publicUri) ||
            (publicUri.Scheme != Uri.UriSchemeHttp && publicUri.Scheme != Uri.UriSchemeHttps))
        {
            yield return new ValidationResult(
                "PublicGatewayUrl của MediaService phải là URL tuyệt đối hợp lệ với scheme http hoặc https.",
                [nameof(PublicGatewayUrl)]);
        }
        else
        {
            if (!string.IsNullOrEmpty(publicUri.UserInfo) ||
                !string.IsNullOrEmpty(publicUri.Query) ||
                !string.IsNullOrEmpty(publicUri.Fragment))
            {
                yield return new ValidationResult(
                    "PublicGatewayUrl của MediaService không được chứa credentials, query string hoặc fragment.",
                    [nameof(PublicGatewayUrl)]);
            }

            var env = validationContext?.GetService<IHostEnvironment>();
            if (env != null && env.IsProduction() && publicUri.Scheme != Uri.UriSchemeHttps)
            {
                yield return new ValidationResult(
                    "PublicGatewayUrl của MediaService trong môi trường Production bắt buộc phải sử dụng giao thức HTTPS.",
                    [nameof(PublicGatewayUrl)]);
            }
        }

        // 3. Kiểm tra ApiKey
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            yield return new ValidationResult("ApiKey của MediaService không được để trống.", [nameof(ApiKey)]);
        }

        // 4. Kiểm tra TimeoutSeconds
        if (TimeoutSeconds <= 0)
        {
            yield return new ValidationResult("TimeoutSeconds phải lớn hơn 0.", [nameof(TimeoutSeconds)]);
        }
    }
}
