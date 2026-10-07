using System.Text.Json.Serialization;

namespace Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

public sealed record SpecificationValidationResult(
    [property: JsonPropertyName("valid"), JsonPropertyOrder(0)] bool Valid,
    [property: JsonPropertyName("errors"), JsonPropertyOrder(2)] IReadOnlyList<SpecificationValidationError> Errors,
    [property: JsonPropertyName("schemaVersion"), JsonPropertyOrder(1)] int? SchemaVersion = null);

public sealed record SpecificationValidationError(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);
