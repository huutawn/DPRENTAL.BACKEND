using System.Text.Json;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Dprental.CatalogService.Domain.Entities;

namespace Dprental.CatalogService.Domain.Validation;

/// <summary>
/// Bộ validation thuần cho giá trị canonical specification của EquipmentType theo schema version đã publish.
/// </summary>
public static class SpecificationValuesValidator
{
    public const string CodeRequired = "REQUIRED";
    public const string CodeTypeMismatch = "TYPE_MISMATCH";
    public const string CodeOutOfRange = "OUT_OF_RANGE";
    public const string CodeInvalidEnumOption = "INVALID_ENUM_OPTION";
    public const string CodeUnknownField = "UNKNOWN_FIELD";

    public const string MessageRequired = "Trường này là bắt buộc.";
    public const string MessageTypeMismatch = "Kiểu dữ liệu không hợp lệ.";
    public const string MessageOutOfRange = "Giá trị nằm ngoài phạm vi cho phép.";
    public const string MessageInvalidEnumOption = "Giá trị không thuộc danh sách tùy chọn hợp lệ.";
    public const string MessageUnknownField = "Trường này không tồn tại trong schema.";

    public static SpecificationValidationResult Validate(
        EquipmentTypeSpecDefinition definition,
        JsonElement specValues)
    {
        List<SpecificationValidationError> errors = [];
        var fields = definition.Fields ?? [];

        Dictionary<string, JsonElement> providedValues = new(StringComparer.Ordinal);
        if (specValues.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in specValues.EnumerateObject())
            {
                providedValues.TryAdd(prop.Name, prop.Value);
            }
        }
        else
        {
            return new SpecificationValidationResult(
                false,
                [new SpecificationValidationError("specValues", CodeTypeMismatch, MessageTypeMismatch)]);
        }

        HashSet<string> schemaKeys = new(StringComparer.Ordinal);

        // 1. Kiểm tra các field trong schema theo thứ tự schema
        foreach (var field in fields)
        {
            schemaKeys.Add(field.Key);
            bool isProvided = providedValues.TryGetValue(field.Key, out var value);

            if (field.Required)
            {
                // Thiếu key
                if (!isProvided)
                {
                    errors.Add(new SpecificationValidationError(field.Key, CodeRequired, MessageRequired));
                    continue;
                }

                // Gửi null
                if (value.ValueKind == JsonValueKind.Null)
                {
                    errors.Add(new SpecificationValidationError(field.Key, CodeRequired, MessageRequired));
                    continue;
                }

                // Chuỗi trắng được xem là thiếu
                if (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()))
                {
                    errors.Add(new SpecificationValidationError(field.Key, CodeRequired, MessageRequired));
                    continue;
                }

                // false và 0 được xem là đã cung cấp và tiếp tục kiểm tra kiểu dữ liệu
            }
            else
            {
                // Field optional được bỏ key
                if (!isProvided)
                {
                    continue;
                }

                // Field optional gửi null -> TYPE_MISMATCH
                if (value.ValueKind == JsonValueKind.Null)
                {
                    errors.Add(new SpecificationValidationError(field.Key, CodeTypeMismatch, MessageTypeMismatch));
                    continue;
                }
            }

            var fieldError = ValidateFieldValue(field, value);
            if (fieldError != null)
            {
                errors.Add(fieldError);
            }
        }

        // 2. Thu thập unknown fields (key ngoài schema), sắp xếp theo key (Ordinal)
        List<string> unknownKeys = [];
        foreach (var key in providedValues.Keys)
        {
            if (!schemaKeys.Contains(key))
            {
                unknownKeys.Add(key);
            }
        }

        unknownKeys.Sort(StringComparer.Ordinal);
        foreach (var unknownKey in unknownKeys)
        {
            errors.Add(new SpecificationValidationError(unknownKey, CodeUnknownField, MessageUnknownField));
        }

        return new SpecificationValidationResult(errors.Count == 0, errors);
    }

    private static SpecificationValidationError? ValidateFieldValue(
        EquipmentTypeSpecField field,
        JsonElement value)
    {
        switch (field.DataType)
        {
            case "number":
                if (value.ValueKind != JsonValueKind.Number)
                {
                    return new SpecificationValidationError(field.Key, CodeTypeMismatch, MessageTypeMismatch);
                }

                if (!value.TryGetDecimal(out decimal numValue))
                {
                    return new SpecificationValidationError(field.Key, CodeOutOfRange, MessageOutOfRange);
                }

                if (field.Min.HasValue && numValue < field.Min.Value)
                {
                    return new SpecificationValidationError(field.Key, CodeOutOfRange, MessageOutOfRange);
                }

                if (field.Max.HasValue && numValue > field.Max.Value)
                {
                    return new SpecificationValidationError(field.Key, CodeOutOfRange, MessageOutOfRange);
                }

                return null;

            case "text":
                if (value.ValueKind != JsonValueKind.String)
                {
                    return new SpecificationValidationError(field.Key, CodeTypeMismatch, MessageTypeMismatch);
                }

                return null;

            case "boolean":
                if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    return new SpecificationValidationError(field.Key, CodeTypeMismatch, MessageTypeMismatch);
                }

                return null;

            case "enum":
                if (value.ValueKind != JsonValueKind.String)
                {
                    return new SpecificationValidationError(field.Key, CodeTypeMismatch, MessageTypeMismatch);
                }

                string strValue = value.GetString()!;
                bool matchFound = false;
                if (field.Options != null)
                {
                    foreach (var option in field.Options)
                    {
                        if (string.Equals(option.Value, strValue, StringComparison.Ordinal))
                        {
                            matchFound = true;
                            break;
                        }
                    }
                }

                if (!matchFound)
                {
                    return new SpecificationValidationError(field.Key, CodeInvalidEnumOption, MessageInvalidEnumOption);
                }

                return null;

            default:
                return new SpecificationValidationError(field.Key, CodeTypeMismatch, MessageTypeMismatch);
        }
    }
}
