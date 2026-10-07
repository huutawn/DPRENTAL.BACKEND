using Dprental.CatalogService.Domain.ValueObjects;

namespace CatalogService.Application.Mapping;

public static class SpecDataTypeMapper
{
    public static string ToContract(SpecDataType type) => type switch
    {
        SpecDataType.Number => "number",
        SpecDataType.WholeNumber => "integer",
        SpecDataType.Boolean => "boolean",
        SpecDataType.Enum => "enum",
        SpecDataType.Text => "text",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    public static bool TryParse(string? value, out SpecDataType type)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "number": type = SpecDataType.Number; return true;
            case "integer": type = SpecDataType.WholeNumber; return true;
            case "boolean": type = SpecDataType.Boolean; return true;
            case "enum": type = SpecDataType.Enum; return true;
            case "text": type = SpecDataType.Text; return true;
            default: type = default; return false;
        }
    }
}