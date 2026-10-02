using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.CatalogService.Domain.Entities;

using Dprental.CatalogService.Common.Enums;

/// <summary>
/// Một loại thiết bị chuẩn của nền tảng (Canonical Catalog), do Admin quản lý.
/// Không chứa dữ liệu của Supplier như organization, giá, khu vực, ảnh hay tình trạng sẵn có.
/// </summary>
[Table("EquipmentTypes", Schema = "catalog")]
// Mã là định danh canonical, duy nhất trên toàn hệ thống.
[Index(nameof(Code), IsUnique = true)]
// Trong một category không có hai type cùng tên (so sánh không phân biệt hoa thường).
[Index(nameof(CategoryId), nameof(NormalizedName), IsUnique = true)]
[Index(nameof(Status))]
public class EquipmentType
{
    /// <summary>Id do ứng dụng tự gán, để EF luôn INSERT entity mới thay vì đoán theo giá trị khóa.</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid Id { get; set; }

    public Guid CategoryId { get; set; }

    /// <summary>Không cho xóa category đang có equipment type.</summary>
    [ForeignKey(nameof(CategoryId))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public Category Category { get; set; } = null!;

    /// <summary>Mã canonical, luôn lưu dạng viết hoa. Bị khóa sau lần publish đầu tiên.</summary>
    [Required]
    [MaxLength(50)]
    public string Code { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Name { get; private set; } = string.Empty;

    /// <summary>Tên đã chuẩn hóa (gộp khoảng trắng, viết hoa) để chặn trùng tên trong category.</summary>
    [Required]
    [MaxLength(200)]
    public string NormalizedName { get; private set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    /// <summary>Type yêu cầu asset khai báo thông số theo một SpecSchema; phải có schema đã publish mới publish được type.</summary>
    public bool RequiresSpecSchema { get; set; }

    /// <summary>Trạng thái vòng đời, lưu dạng chuỗi và chỉ thay đổi qua <see cref="Publish"/> và <see cref="Disable"/>.</summary>
    [Column(TypeName = "character varying(20)")]
    [MaxLength(20)]
    public EquipmentTypeStatus Status { get; private set; } = EquipmentTypeStatus.Draft;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>Thời điểm publish gần nhất. Khác null nghĩa là type đã từng công khai và có thể đã được tham chiếu.</summary>
    public DateTime? PublishedAtUtc { get; private set; }

    /// <summary>Thời điểm disable gần nhất; null khi đang không bị disable.</summary>
    public DateTime? DisabledAtUtc { get; private set; }

    /// <summary>
    /// Ánh xạ vào cột hệ thống <c>xmin</c> của PostgreSQL, tự đổi mỗi lần dòng được ghi.
    /// Nhờ đó hai request sửa cùng một type đồng thời không ghi đè lên nhau: request lưu sau bị từ chối.
    /// </summary>
    [Timestamp]
    public uint Version { get; private set; }

    /// <summary>Type đã từng publish thì bên ngoài có thể đã tham chiếu theo mã, nên mã không được đổi nữa.</summary>
    public bool IsCodeLocked => PublishedAtUtc is not null;

    /// <summary>Đổi tên và cập nhật luôn dạng chuẩn hóa dùng để chặn trùng.</summary>
    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name.Trim();
        NormalizedName = NormalizeName(name);
    }

    /// <summary>Công khai type từ trạng thái Draft hoặc Disabled.</summary>
    public void Publish(DateTime utcNow)
    {
        if (Status == EquipmentTypeStatus.Published)
        {
            throw new InvalidOperationException("Equipment type đang ở trạng thái Published.");
        }

        utcNow = AsUtc(utcNow);
        Status = EquipmentTypeStatus.Published;
        PublishedAtUtc = utcNow;
        DisabledAtUtc = null;
        UpdatedAtUtc = utcNow;
    }

    /// <summary>Ngừng dùng type đang Published; dữ liệu và các tham chiếu bên ngoài được giữ nguyên.</summary>
    public void Disable(DateTime utcNow)
    {
        if (Status != EquipmentTypeStatus.Published)
        {
            throw new InvalidOperationException("Chỉ có thể disable equipment type đang ở trạng thái Published.");
        }

        utcNow = AsUtc(utcNow);
        Status = EquipmentTypeStatus.Disabled;
        DisabledAtUtc = utcNow;
        UpdatedAtUtc = utcNow;
    }

    /// <summary>Gộp khoảng trắng thừa và viết hoa, để "Forklift  1.5t" và "forklift 1.5T" được coi là một tên.</summary>
    public static string NormalizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }

    /// <summary>PostgreSQL timestamptz chỉ nhận DateTime có Kind = Utc, nên chuẩn hóa trước khi lưu.</summary>
    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
}
