using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.CatalogService.Domain.Entities;

/// <summary>NhÃ³m phÃ¢n loáº¡i equipment type. MVP dÃ¹ng danh sÃ¡ch pháº³ng, chÆ°a cÃ³ phÃ¢n cáº¥p.</summary>
[Table("Categories", Schema = "catalog")]
[Index(nameof(Slug), IsUnique = true)]
public class Category
{
    /// <summary>Id do á»©ng dá»¥ng tá»± gÃ¡n, Ä‘á»ƒ EF luÃ´n INSERT entity má»›i thay vÃ¬ Ä‘oÃ¡n theo giÃ¡ trá»‹ khÃ³a.</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Äá»‹nh danh dáº¡ng URL-friendly, duy nháº¥t trong toÃ n bá»™ catalog.</summary>
    [Required]
    [MaxLength(150)]
    public string Slug { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>Category ngá»«ng hoáº¡t Ä‘á»™ng khÃ´ng nháº­n equipment type má»›i vÃ  khÃ´ng cho publish.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>áº¢nh Ä‘áº¡i diá»‡n cá»§a category, tham chiáº¿u logic tá»›i MediaService.</summary>
    public Guid? ImageMediaId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAtUtc { get; set; }

    public ICollection<EquipmentType> EquipmentTypes { get; set; } = new List<EquipmentType>();
    /// <summary>
    /// Chuyá»ƒn Ä‘á»•i tráº¡ng thÃ¡i hoáº¡t Ä‘á»™ng cá»§a category. CÃ³ tÃ­nh idempotent:
    /// náº¿u tráº¡ng thÃ¡i khÃ´ng thay Ä‘á»•i thÃ¬ khÃ´ng cáº­p nháº­t UpdatedAtUtc vÃ  tráº£ vá» false.
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
    /// VÃ´ hiá»‡u hÃ³a category. CÃ³ tÃ­nh idempotent: náº¿u Ä‘Ã£ inactive thÃ¬ khÃ´ng thay Ä‘á»•i UpdatedAtUtc.
    /// Tráº£ vá» true náº¿u tráº¡ng thÃ¡i thá»±c sá»± thay Ä‘á»•i.
    /// </summary>
    public bool Disable(DateTime utcNow) => TransitionStatus(false, utcNow);

    /// <summary>
    /// KÃ­ch hoáº¡t category. CÃ³ tÃ­nh idempotent: náº¿u Ä‘Ã£ active thÃ¬ khÃ´ng thay Ä‘á»•i UpdatedAtUtc.
    /// Tráº£ vá» true náº¿u tráº¡ng thÃ¡i thá»±c sá»± thay Ä‘á»•i.
    /// </summary>
    public bool Enable(DateTime utcNow) => TransitionStatus(true, utcNow);

    /// <summary>
    /// Cáº­p nháº­t thá»i Ä‘iá»ƒm sá»­a Ä‘á»•i cá»§a category theo chuáº©n UTC.
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
