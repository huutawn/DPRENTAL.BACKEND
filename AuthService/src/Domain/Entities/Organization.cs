using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Entities;

using Dprental.AuthService.Common.Enums;

/// <summary>Một tổ chức (Platform/Supplier/Renter) mà user có thể là thành viên.</summary>
[Table("Organizations", Schema = "auth")]
[Index(nameof(Slug), IsUnique = true)]
[Index(nameof(TaxCode), IsUnique = true)]
[Index(nameof(Status))]
public class Organization
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Định danh dạng URL-friendly, ổn định, dùng để tham chiếu thay vì Id trong URL public.</summary>
    [Required]
    [MaxLength(100)]
    public string Slug { get; set; } = string.Empty;

    public OrganizationType Type { get; set; }

    public OrganizationStatus Status { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    // Thông tin pháp lý — bắt buộc khi tự đăng ký, có thể trống với organization do Platform tạo trực tiếp.

    /// <summary>Mã số thuế / mã số doanh nghiệp (10 số, hoặc 13 số dạng 10-3 cho chi nhánh).</summary>
    [MaxLength(14)]
    public string? TaxCode { get; set; }

    [MaxLength(50)]
    public string? BusinessLicenseNumber { get; set; }

    public DateOnly? BusinessLicenseIssuedDate { get; set; }

    [MaxLength(200)]
    public string? BusinessLicenseIssuedPlace { get; set; }

    public DateOnly? EstablishedDate { get; set; }

    [MaxLength(100)]
    public string? LegalRepresentativeName { get; set; }

    [MaxLength(12)]
    public string? LegalRepresentativeCitizenId { get; set; }

    // Liên hệ & địa chỉ

    [MaxLength(254)]
    public string? ContactEmail { get; set; }

    [MaxLength(15)]
    public string? ContactPhone { get; set; }

    [MaxLength(250)]
    public string? Address { get; set; }

    [MaxLength(100)]
    public string? Province { get; set; }

    [MaxLength(100)]
    public string? Ward { get; set; }

    [MaxLength(500)]
    public string? Website { get; set; }

    public Guid? LogoMediaId { get; set; }

    [MaxLength(2000)]
    public string? Description { get; set; }

    // Hồ sơ xác minh & kết quả duyệt

    public Guid? BusinessLicenseDocumentMediaId { get; set; }

    [MaxLength(500)]
    public string? RejectionReason { get; set; }
    [MaxLength(500)]
    public string? SuspensionReason { get; set; }


    public Guid? ReviewedByUserId { get; set; }

    public DateTime? ReviewedAtUtc { get; set; }

    /// <summary>Lần gần nhất Owner gửi lại hồ sơ sau khi bị từ chối; null nếu chưa từng gửi lại (khi đó tính từ <see cref="CreatedAtUtc"/>).</summary>
    public DateTime? ResubmittedAtUtc { get; set; }

    /// <summary>Lần gần nhất Owner nhắc Platform duyệt hồ sơ đã quá hạn; xoá khi hồ sơ được gửi lại.</summary>
    public DateTime? LastRemindedAtUtc { get; set; }

    public ICollection<OrganizationMembership> Memberships { get; set; } = new List<OrganizationMembership>();
}
