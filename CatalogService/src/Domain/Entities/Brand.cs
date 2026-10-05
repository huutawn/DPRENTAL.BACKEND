namespace Dprental.CatalogService.Domain.Entities;

/// <summary>
/// Đại diện cho thương hiệu thiết bị trong Catalog, dùng chung toàn platform.
/// </summary>
public class Brand
{
    /// <summary>Id do ứng dụng tự gán.</summary>
    public Guid Id { get; set; }

    /// <summary>Mã thương hiệu duy nhất trong toàn bộ Catalog (trim, uppercase, tối đa 50 ký tự).</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Tên thương hiệu (Unicode NFC, trim/gom khoảng trắng, tối đa 200 ký tự).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Định danh URL-friendly duy nhất trong Catalog (trim, lowercase, tối đa 150 ký tự).</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Ảnh logo của thương hiệu, tham chiếu logic tới MediaService.</summary>
    public Guid? LogoMediaId { get; set; }

    /// <summary>Trạng thái hoạt động của thương hiệu. Tạo mới mặc định active.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Thời điểm tạo theo chuẩn UTC.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Thời điểm cập nhật gần nhất theo chuẩn UTC.</summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Vô hiệu hóa thương hiệu. Có tính idempotent: nếu đã inactive thì không thay đổi UpdatedAt.
    /// Trả về true nếu trạng thái thực sự thay đổi.
    /// </summary>
    public bool Disable(DateTime utcNow)
    {
        if (!IsActive)
        {
            return false;
        }

        IsActive = false;
        UpdatedAt = AsUtc(utcNow);
        return true;
    }

    /// <summary>
    /// Kích hoạt thương hiệu. Có tính idempotent: nếu đã active thì không thay đổi UpdatedAt.
    /// Trả về true nếu trạng thái thực sự thay đổi.
    /// </summary>
    public bool Enable(DateTime utcNow)
    {
        if (IsActive)
        {
            return false;
        }

        IsActive = true;
        UpdatedAt = AsUtc(utcNow);
        return true;
    }

    /// <summary>
    /// Cập nhật thời điểm sửa đổi của thương hiệu.
    /// </summary>
    public void Touch(DateTime utcNow)
    {
        UpdatedAt = AsUtc(utcNow);
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
}
