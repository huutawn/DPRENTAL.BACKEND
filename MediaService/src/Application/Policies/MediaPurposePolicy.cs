namespace Dprental.MediaService.Application.Policies;

/// <summary>
/// Định nghĩa chính sách tải tệp áp dụng cho từng mục đích sử dụng (purpose).
/// </summary>
public sealed record MediaPurposePolicy(
    string Purpose,
    string OwnerService,
    string OwnerEntityType,
    IReadOnlySet<string> AllowedContentTypes,
    long MaxSizeBytes,
    bool AllowsPublic)
{
    public const long TenMegabytes = 10L * 1024 * 1024;
    public const long TwentyMegabytes = 20L * 1024 * 1024;

    public const string EquipmentImage = "equipment-image";
    public const string OrganizationLogo = "organization-logo";
    public const string EquipmentDocument = "equipment-document";

    private static readonly IReadOnlySet<string> StandardImageContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp"
    };

    private static readonly IReadOnlySet<string> StandardDocumentContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf"
    };

    private static readonly Dictionary<string, MediaPurposePolicy> Policies = new(StringComparer.OrdinalIgnoreCase)
    {
        [EquipmentImage] = new(EquipmentImage, "CatalogService", "Equipment", StandardImageContentTypes, TenMegabytes, true),
        [OrganizationLogo] = new(OrganizationLogo, "OrganizationService", "Organization", StandardImageContentTypes, TenMegabytes, true),
        [EquipmentDocument] = new(EquipmentDocument, "CatalogService", "Equipment", StandardDocumentContentTypes, TwentyMegabytes, false)
    };

    /// <summary>
    /// Tìm chính sách theo mục đích sử dụng. Trả về null nếu mục đích chưa được hỗ trợ / cấu hình.
    /// </summary>
    public static MediaPurposePolicy? GetPolicy(string? purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose))
        {
            return null;
        }

        return Policies.TryGetValue(purpose.Trim(), out var policy) ? policy : null;
    }

    /// <summary>
    /// Kiểm tra xem MIME type có nằm trong danh sách cho phép của policy hay không.
    /// </summary>
    public bool IsContentTypeAllowed(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        return AllowedContentTypes.Contains(contentType.Trim());
    }

    /// <summary>
    /// Kiểm tra xem dung lượng tệp có hợp lệ theo policy hay không (lớn hơn 0 và không vượt quá MaxSizeBytes).
    /// </summary>
    public bool IsSizeAllowed(long sizeBytes)
    {
        return sizeBytes > 0 && sizeBytes <= MaxSizeBytes;
    }
}
