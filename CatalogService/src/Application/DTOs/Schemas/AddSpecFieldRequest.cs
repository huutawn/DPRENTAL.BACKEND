using System.ComponentModel.DataAnnotations;

namespace CatalogService.Application.DTOs.Schemas;

public sealed class OptionRequest
{
    [Required]
    [MaxLength(100)]
    public string Value { get; init; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Label { get; init; } = string.Empty;
}

public sealed class AddSpecFieldRequest : IValidatableObject
{
    [Required]
    [MaxLength(64)]
    public string Key { get; init; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Label { get; init; } = string.Empty;

    /// <summary>number | integer | boolean | enum | text</summary>
    [Required]
    public string DataType { get; init; } = string.Empty;

    [MaxLength(50)]
    public string? Unit { get; init; }

    public bool IsRequired { get; init; }

    public decimal? MinValue { get; init; }

    public decimal? MaxValue { get; init; }

    /// <summary>Bắt buộc có khi dataType là enum, không được có với kiểu khác.</summary>
    public List<OptionRequest>? Options { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MinValue is not null && MaxValue is not null && MinValue > MaxValue)
        {
            yield return new ValidationResult(
                "MinValue không được lớn hơn MaxValue.", [nameof(MinValue), nameof(MaxValue)]);
        }
    }
}