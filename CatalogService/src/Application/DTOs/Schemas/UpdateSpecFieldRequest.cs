using System.ComponentModel.DataAnnotations;

namespace Dprental.CatalogService.Application.DTOs.Schemas;

public sealed class UpdateSpecFieldRequest : IValidatableObject
{
    [Required]
    [MaxLength(200)]
    public string Label { get; init; } = string.Empty;

    [MaxLength(50)]
    public string? Unit { get; init; }

    public bool IsRequired { get; init; }

    public decimal? MinValue { get; init; }

    public decimal? MaxValue { get; init; }

    [Range(0, int.MaxValue)]
    public int DisplayOrder { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MinValue is not null && MaxValue is not null && MinValue > MaxValue)
        {
            yield return new ValidationResult(
                "MinValue không được lớn hơn MaxValue.",
                [nameof(MinValue), nameof(MaxValue)]);
        }
    }
}