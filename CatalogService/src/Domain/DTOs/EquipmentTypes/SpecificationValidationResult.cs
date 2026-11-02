using System.Text.Json.Serialization;

namespace Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

public sealed record SpecificationValidationResult(
    [property: JsonPropertyOrder(0)] bool Valid,
    [property: JsonPropertyOrder(2)] IReadOnlyList<SpecificationValidationError> Errors,
    [property: JsonPropertyOrder(1)] int? SchemaVersion = null);

public sealed record SpecificationValidationError(
    string Field,
    string Code,
    string Message);
