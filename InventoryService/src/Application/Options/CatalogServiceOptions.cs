using System.ComponentModel.DataAnnotations;

namespace Dprental.InventoryService.Application.Options;

/// <summary>
/// Cấu hình kết nối tới CatalogService.
/// </summary>
public sealed class CatalogServiceOptions : IValidatableObject
{
    public const string SectionName = "CatalogService";

    /// <summary>
    /// Địa chỉ cơ sở của CatalogService (ví dụ: http://catalog-service:8080).
    /// </summary>
    [Required(ErrorMessage = "BaseUrl của CatalogService không được để trống.")]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Thời gian chờ gọi HTTP (mặc định: 10 giây).
    /// </summary>
    [Range(1, 300, ErrorMessage = "TimeoutSeconds phải lớn hơn 0 và tối đa 300 giây.")]
    public int TimeoutSeconds { get; set; } = 10;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            yield return new ValidationResult("BaseUrl của CatalogService không được để trống.", [nameof(BaseUrl)]);
            yield break;
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            yield return new ValidationResult(
                "BaseUrl của CatalogService phải là URL tuyệt đối hợp lệ với scheme http hoặc https.",
                [nameof(BaseUrl)]);
        }
        else
        {
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                yield return new ValidationResult(
                    "BaseUrl của CatalogService không được chứa thông tin xác thực (credentials).",
                    [nameof(BaseUrl)]);
            }

            if (!string.IsNullOrEmpty(uri.Query))
            {
                yield return new ValidationResult(
                    "BaseUrl của CatalogService không được chứa query string.",
                    [nameof(BaseUrl)]);
            }

            if (!string.IsNullOrEmpty(uri.Fragment))
            {
                yield return new ValidationResult(
                    "BaseUrl của CatalogService không được chứa fragment.",
                    [nameof(BaseUrl)]);
            }
        }

        if (TimeoutSeconds <= 0)
        {
            yield return new ValidationResult(
                "TimeoutSeconds phải là số nguyên dương lớn hơn 0.",
                [nameof(TimeoutSeconds)]);
        }
    }
}
