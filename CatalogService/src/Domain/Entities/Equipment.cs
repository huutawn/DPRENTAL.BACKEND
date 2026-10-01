using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.CatalogService.Domain.Entities;

using Dprental.CatalogService.Common.Enums;

/// <summary>Một equipment listing thuộc sở hữu của một organization. Đây là aggregate chính của Catalog.</summary>
[Table("Equipments", Schema = "catalog")]
// Code chỉ duy nhất trong phạm vi organization; index này cũng phục vụ truy vấn theo OrganizationId.
[Index(nameof(OrganizationId), nameof(Code), IsUnique = true)]
[Index(nameof(Status))]
[Index(nameof(CategoryId))]
[Index(nameof(RegionId))]
[Index(nameof(DailyPrice))]
[Index(nameof(CreatedAtUtc))]
[Index(nameof(PublishedAtUtc))]
public class Equipment
{
    /// <summary>Giá trị JSON mặc định khi equipment chưa khai báo thông số kỹ thuật.</summary>
    public const string EmptySpecifications = "{}";

    /// <summary>Id do ứng dụng tự gán, để EF luôn INSERT entity mới thay vì đoán theo giá trị khóa.</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid Id { get; set; }

    /// <summary>Organization sở hữu listing. Chỉ là tham chiếu logic tới Auth Service, không có FK.</summary>
    public Guid OrganizationId { get; set; }

    public Guid CategoryId { get; set; }

    /// <summary>Không cho xóa category đang có equipment.</summary>
    [ForeignKey(nameof(CategoryId))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public Category Category { get; set; } = null!;

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Mã thiết bị do organization tự đặt, duy nhất trong phạm vi organization.</summary>
    [Required]
    [MaxLength(50)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? Brand { get; set; }

    [MaxLength(4000)]
    public string? Description { get; set; }

    /// <summary>Giá thuê theo ngày.</summary>
    [Precision(18, 2)]
    public decimal DailyPrice { get; set; }

    /// <summary>Khu vực cho thuê. Chỉ là tham chiếu logic, chưa có bảng region trong Catalog.</summary>
    public Guid RegionId { get; set; }

    /// <summary>Trạng thái vòng đời, lưu dạng chuỗi và chỉ thay đổi qua <see cref="Publish"/> và <see cref="Hide"/>.</summary>
    [Column(TypeName = "character varying(20)")]
    [MaxLength(20)]
    public EquipmentStatus Status { get; private set; } = EquipmentStatus.Draft;

    /// <summary>Thông số kỹ thuật dạng JSON object, khác nhau giữa các loại equipment.</summary>
    [Required]
    [Column(TypeName = "jsonb")]
    public string Specifications { get; set; } = EmptySpecifications;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>Thời điểm publish gần nhất. Null khi listing chưa từng được publish.</summary>
    public DateTime? PublishedAtUtc { get; private set; }

    public ICollection<EquipmentMedia> Media { get; set; } = new List<EquipmentMedia>();

    /// <summary>Đưa listing lên public catalog từ trạng thái Draft hoặc Hidden.</summary>
    public void Publish(DateTime utcNow)
    {
        if (Status == EquipmentStatus.Published)
        {
            throw new InvalidOperationException("Equipment đang ở trạng thái Published.");
        }

        utcNow = AsUtc(utcNow);
        Status = EquipmentStatus.Published;
        PublishedAtUtc = utcNow;
        UpdatedAtUtc = utcNow;
    }

    /// <summary>Ẩn listing đang Published khỏi public catalog.</summary>
    public void Hide(DateTime utcNow)
    {
        if (Status != EquipmentStatus.Published)
        {
            throw new InvalidOperationException("Chỉ có thể ẩn equipment đang ở trạng thái Published.");
        }

        Status = EquipmentStatus.Hidden;
        UpdatedAtUtc = AsUtc(utcNow);
    }

    /// <summary>PostgreSQL timestamptz chỉ nhận DateTime có Kind = Utc, nên chuẩn hóa trước khi lưu.</summary>
    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
}
