using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Hosting;

namespace Dprental.InventoryService.Application.Media;

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
                "InternalBaseUrl phải là absolute HTTP/HTTPS URL hợp lệ.",
                [nameof(InternalBaseUrl)]);
        }
        else
        {
            if (!string.IsNullOrEmpty(internalUri.UserInfo))
            {
                yield return new ValidationResult(
                    "InternalBaseUrl không được chứa thông tin xác thực (credentials).",
                    [nameof(InternalBaseUrl)]);
            }

            if (!string.IsNullOrEmpty(internalUri.Query))
            {
                yield return new ValidationResult(
                    "InternalBaseUrl không được chứa query string.",
                    [nameof(InternalBaseUrl)]);
            }

            if (!string.IsNullOrEmpty(internalUri.Fragment))
            {
                yield return new ValidationResult(
                    "InternalBaseUrl không được chứa fragment.",
                    [nameof(InternalBaseUrl)]);
            }
        }

        // 2. Kiểm tra PublicGatewayUrl
        if (string.IsNullOrWhiteSpace(PublicGatewayUrl) ||
            !Uri.TryCreate(PublicGatewayUrl, UriKind.Absolute, out var publicUri) ||
            (publicUri.Scheme != Uri.UriSchemeHttp && publicUri.Scheme != Uri.UriSchemeHttps))
        {
            yield return new ValidationResult(
                "PublicGatewayUrl phải là absolute HTTP/HTTPS URL hợp lệ.",
                [nameof(PublicGatewayUrl)]);
        }
        else
        {
            if (!string.IsNullOrEmpty(publicUri.UserInfo))
            {
                yield return new ValidationResult(
                    "PublicGatewayUrl không được chứa thông tin xác thực (credentials).",
                    [nameof(PublicGatewayUrl)]);
            }

            if (!string.IsNullOrEmpty(publicUri.Query))
            {
                yield return new ValidationResult(
                    "PublicGatewayUrl không được chứa query string.",
                    [nameof(PublicGatewayUrl)]);
            }

            if (!string.IsNullOrEmpty(publicUri.Fragment))
            {
                yield return new ValidationResult(
                    "PublicGatewayUrl không được chứa fragment.",
                    [nameof(PublicGatewayUrl)]);
            }

            var env = validationContext?.GetService(typeof(IHostEnvironment)) as IHostEnvironment;
            if (env is not null && env.IsProduction() && publicUri.Scheme != Uri.UriSchemeHttps)
            {
                yield return new ValidationResult(
                    "PublicGatewayUrl phải sử dụng HTTPS trong môi trường Production.",
                    [nameof(PublicGatewayUrl)]);
            }
        }

        // 3. Kiểm tra ApiKey
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            yield return new ValidationResult(
                "ApiKey của MediaService không được để trống.",
                [nameof(ApiKey)]);
        }

        // 4. Kiểm tra TimeoutSeconds
        if (TimeoutSeconds <= 0)
        {
            yield return new ValidationResult(
                "TimeoutSeconds phải lớn hơn 0.",
                [nameof(TimeoutSeconds)]);
        }
    }
}
