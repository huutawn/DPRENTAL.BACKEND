using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.CatalogService.Domain.Entities;

/// <summary>Nhóm phân loại equipment type. MVP dùng danh sách phẳng, chưa có phân cấp.</summary>
[Table("Categories", Schema = "catalog")]
[Index(nameof(Slug), IsUnique = true)]
public class Category
{
    /// <summary>Id do ứng dụng tự gán, để EF luôn INSERT entity mới thay vì đoán theo giá trị khóa.</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Định danh dạng URL-friendly, duy nhất trong toàn bộ catalog.</summary>
    [Required]
    [MaxLength(150)]
    public string Slug { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>Category ngừng hoạt động không nhận equipment type mới và không cho publish.</summary>
    public bool IsActive { get; set; } = true;
    /// <summary>Ảnh đại diện của category, tham chiếu logic tới MediaService.</summary>
    public Guid? ImageMediaId { get; set; }


    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAtUtc { get; set; }

    public ICollection<EquipmentType> EquipmentTypes { get; set; } = new List<EquipmentType>();

    /// <summary>
    /// Chuyển đổi trạng thái hoạt động của category. Có tính idempotent:
    /// nếu trạng thái không thay đổi thì không cập nhật UpdatedAtUtc và trả về false.
    /// </summary>
    public bool TransitionStatus(bool targetActive, DateTime utcNow)
    {
        if (IsActive == targetActive)
        {
            return false;
        }

        IsActive = targetActive;
        UpdatedAtUtc = AsUtc(utcNow);
        return true;
    }

    /// <summary>
    /// Vô hiệu hóa category. Có tính idempotent: nếu đã inactive thì không thay đổi UpdatedAtUtc.
    /// Trả về true nếu trạng thái thực sự thay đổi.
    /// </summary>
    public bool Disable(DateTime utcNow) => TransitionStatus(false, utcNow);

    /// <summary>
    /// Kích hoạt category. Có tính idempotent: nếu đã active thì không thay đổi UpdatedAtUtc.
    /// Trả về true nếu trạng thái thực sự thay đổi.
    /// </summary>
    public bool Enable(DateTime utcNow) => TransitionStatus(true, utcNow);

    /// <summary>
    /// Cập nhật thời điểm sửa đổi của category theo chuẩn UTC.
    /// </summary>
    public void Touch(DateTime utcNow)
    {
        UpdatedAtUtc = AsUtc(utcNow);
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
}
