using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.RentalService.Domain.Entities;

/// <summary>
/// Bảng mức hư hại cố định: mỗi mức quy ra phần trăm tiền cọc của một máy bị khấu trừ.
/// Chỉ là dữ liệu tra cứu; công thức tính tiền thuộc Application.
/// </summary>
[Table("damage_levels", Schema = "rental")]
public class DamageLevel
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Level { get; private set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; private set; } = string.Empty;

    [Required]
    [MaxLength(500)]
    public string Description { get; private set; } = string.Empty;

    [Range(0, 100)]
    public int Percent { get; private set; }

    private DamageLevel()
    {
    }

    internal DamageLevel(int level, string name, string description, int percent)
    {
        Level = level;
        Name = name;
        Description = description;
        Percent = percent;
    }
}
