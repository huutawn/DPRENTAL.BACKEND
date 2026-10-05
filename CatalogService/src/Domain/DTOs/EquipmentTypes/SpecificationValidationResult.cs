namespace Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

public sealed record SpecificationValidationResult(
    bool Valid,
    IReadOnlyList<SpecificationValidationError> Errors);

public sealed record SpecificationValidationError(
    string Field,
    string Code,
    string Message);
