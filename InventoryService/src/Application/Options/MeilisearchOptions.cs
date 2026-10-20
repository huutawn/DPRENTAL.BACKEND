using System.ComponentModel.DataAnnotations;

namespace Dprental.InventoryService.Application.Options;

/// <summary>
/// Cấu hình kết nối và các chỉ mục Meilisearch cho InventoryService.
/// </summary>
public sealed class MeilisearchOptions : IValidatableObject
{
    public const string SectionName = "Meilisearch";

    /// <summary>
    /// Địa chỉ máy chủ Meilisearch (ví dụ: http://localhost:7700 hoặc http://meilisearch:7700).
    /// </summary>
    [Required(ErrorMessage = "Url của Meilisearch không được để trống.")]
    public string Url { get; set; } = "http://localhost:7700";

    /// <summary>
    /// API Key xác thực với Meilisearch (để trống nếu không bật auth ở môi trường dev).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Thời gian chờ gọi Meilisearch (mặc định: 5 giây).
    /// </summary>
    [Range(1, 60, ErrorMessage = "TimeoutSeconds phải lớn hơn 0 và tối đa 60 giây.")]
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Thời hạn tối đa để theo dõi Meilisearch task bất đồng bộ (mặc định: 60 giây).
    /// </summary>
    [Range(5, 300, ErrorMessage = "TaskDeadlineSeconds phải từ 5 đến 300 giây.")]
    public int TaskDeadlineSeconds { get; set; } = 60;

    /// <summary>
    /// Giới hạn số lượng bản ghi ứng viên lấy về từ Meilisearch (mặc định: 5000).
    /// Nếu số hits chạm ngưỡng này, hệ thống sẽ phát hiện kết quả bị cắt và fallback sang SQL.
    /// </summary>
    [Range(10, 100000, ErrorMessage = "CandidateLimit phải từ 10 đến 100.000.")]
    public int CandidateLimit { get; set; } = 5000;

    /// <summary>
    /// Tên index danh mục listing công khai. Không chứa ownerId, serial, assetId.
    /// </summary>
    public string PublicListingsIndex { get; set; } = "inventory_public_listings";

    /// <summary>
    /// Tên index danh mục listing do chủ sở hữu quản lý.
    /// </summary>
    public string OwnerListingsIndex { get; set; } = "inventory_owner_listings";

    /// <summary>
    /// Tên index danh mục thiết bị vật lý cá nhân do chủ sở hữu quản lý.
    /// </summary>
    public string OwnerAssetsIndex { get; set; } = "inventory_owner_assets";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Url))
        {
            yield return new ValidationResult("Url của Meilisearch không được để trống.", [nameof(Url)]);
            yield break;
        }

        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            yield return new ValidationResult(
                "Url của Meilisearch phải là URL tuyệt đối hợp lệ với scheme http hoặc https.",
                [nameof(Url)]);
        }

        if (string.IsNullOrWhiteSpace(PublicListingsIndex))
        {
            yield return new ValidationResult("PublicListingsIndex không được để trống.", [nameof(PublicListingsIndex)]);
        }

        if (string.IsNullOrWhiteSpace(OwnerListingsIndex))
        {
            yield return new ValidationResult("OwnerListingsIndex không được để trống.", [nameof(OwnerListingsIndex)]);
        }

        if (string.IsNullOrWhiteSpace(OwnerAssetsIndex))
        {
            yield return new ValidationResult("OwnerAssetsIndex không được để trống.", [nameof(OwnerAssetsIndex)]);
        }

        var indexes = new[] { PublicListingsIndex, OwnerListingsIndex, OwnerAssetsIndex }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        if (indexes.Distinct(StringComparer.OrdinalIgnoreCase).Count() < indexes.Count)
        {
            yield return new ValidationResult("Tên các chỉ mục Meilisearch không được trùng nhau.",
                [nameof(PublicListingsIndex), nameof(OwnerListingsIndex), nameof(OwnerAssetsIndex)]);
        }
    }
}
