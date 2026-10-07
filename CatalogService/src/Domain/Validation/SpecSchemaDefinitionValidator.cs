using System.Text.Json;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.Entities;

namespace Dprental.CatalogService.Domain.Validation;

/// <summary>
/// Bộ validation authoritative dùng chung cho Create, Edit Draft và Publish specification schema.
/// </summary>
public static class SpecSchemaDefinitionValidator
{
    private static readonly HashSet<string> AllowedDataTypes = new(StringComparer.Ordinal)
    {
        "number",
        "integer",
        "text",
        "boolean",
        "enum"
    };

    private static readonly HashSet<string> AllowedFieldPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "key",
        "label",
        "dataType",
        "required",
        "filterable",
        "primary",
        "displayOrder",
        "min",
        "max",
        "options",
        "unit",
        "description",
        "metadata"
    };

    private static readonly HashSet<string> AllowedOptionPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "value",
        "label"
    };

    public static Result<EquipmentTypeSpecDefinition> Validate(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Result.Failure<EquipmentTypeSpecDefinition>(
                CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Definition phải là JSON object."));
        }

        var rootMap = new Dictionary<string, JsonProperty>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in root.EnumerateObject())
        {
            if (!rootMap.TryAdd(prop.Name, prop))
            {
                return Result.Failure<EquipmentTypeSpecDefinition>(
                    CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail(
                        $"Thuộc tính '{prop.Name}' bị khai báo trùng lặp ở root."));
            }
        }

        JsonProperty fieldsProp;

        // Cho phép gửi trực tiếp { "fields": [...] } hoặc bọc { "definition": { "fields": [...] } }
        if (rootMap.TryGetValue("definition", out var defProp))
        {
            if (rootMap.Count != 1)
            {
                return Result.Failure<EquipmentTypeSpecDefinition>(
                    CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail(
                        "Khi bọc bằng 'definition', root không được chứa thuộc tính nào khác."));
            }

            if (defProp.Value.ValueKind != JsonValueKind.Object)
            {
                return Result.Failure<EquipmentTypeSpecDefinition>(
                    CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("'definition' phải là JSON object."));
            }

            var defMap = new Dictionary<string, JsonProperty>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in defProp.Value.EnumerateObject())
            {
                if (!defMap.TryAdd(p.Name, p))
                {
                    return Result.Failure<EquipmentTypeSpecDefinition>(
                        CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail(
                            $"Thuộc tính '{p.Name}' bị khai báo trùng lặp trong 'definition'."));
                }
            }

            foreach (var propName in defMap.Keys)
            {
                if (!string.Equals(propName, "fields", StringComparison.OrdinalIgnoreCase))
                {
                    return Result.Failure<EquipmentTypeSpecDefinition>(
                        CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail(
                            $"Thuộc tính '{propName}' không thuộc contract của definition."));
                }
            }

            if (!defMap.TryGetValue("fields", out fieldsProp))
            {
                return Result.Failure<EquipmentTypeSpecDefinition>(
                    CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Definition phải chứa mảng 'fields'."));
            }
        }
        else
        {
            foreach (var propName in rootMap.Keys)
            {
                if (!string.Equals(propName, "fields", StringComparison.OrdinalIgnoreCase))
                {
                    return Result.Failure<EquipmentTypeSpecDefinition>(
                        CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail(
                            $"Thuộc tính '{propName}' không thuộc contract của definition."));
                }
            }

            if (!rootMap.TryGetValue("fields", out fieldsProp))
            {
                return Result.Failure<EquipmentTypeSpecDefinition>(
                    CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Definition phải chứa mảng 'fields'."));
            }
        }

        if (fieldsProp.Value.ValueKind != JsonValueKind.Array)
        {
            return Result.Failure<EquipmentTypeSpecDefinition>(
                CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Thuộc tính 'fields' phải là một mảng."));
        }

        var fields = new List<EquipmentTypeSpecField>();

        foreach (var fieldElement in fieldsProp.Value.EnumerateArray())
        {
            if (fieldElement.ValueKind != JsonValueKind.Object)
            {
                return Result.Failure<EquipmentTypeSpecDefinition>(
                    CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Mỗi phần tử trong 'fields' phải là một JSON object."));
            }

            var fieldMap = new Dictionary<string, JsonProperty>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in fieldElement.EnumerateObject())
            {
                if (!fieldMap.TryAdd(prop.Name, prop))
                {
                    return Result.Failure<EquipmentTypeSpecDefinition>(
                        CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail(
                            $"Thuộc tính '{prop.Name}' bị khai báo trùng lặp trong field."));
                }
            }

            foreach (var propName in fieldMap.Keys)
            {
                if (!AllowedFieldPropertyNames.Contains(propName))
                {
                    return Result.Failure<EquipmentTypeSpecDefinition>(
                        CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail(
                            $"Thuộc tính '{propName}' không thuộc contract của field."));
                }
            }

            // 1. Key
            if (!fieldMap.TryGetValue("key", out var keyProp) || keyProp.Value.ValueKind != JsonValueKind.String)
            {
                return Result.Failure<EquipmentTypeSpecDefinition>(
                    CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Field 'key' bắt buộc và phải là chuỗi ký tự."));
            }

            string key = keyProp.Value.GetString()!;

            // 2. Label
            if (!fieldMap.TryGetValue("label", out var labelProp) || labelProp.Value.ValueKind != JsonValueKind.String)
            {
                return Result.Failure<EquipmentTypeSpecDefinition>(
                    CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Field 'label' bắt buộc và phải là chuỗi ký tự."));
            }

            string label = labelProp.Value.GetString()!;

            // 3. DataType
            if (!fieldMap.TryGetValue("dataType", out var dataTypeProp) || dataTypeProp.Value.ValueKind != JsonValueKind.String)
            {
                return Result.Failure<EquipmentTypeSpecDefinition>(
                    CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Field 'dataType' bắt buộc và phải là chuỗi ký tự."));
            }

            string dataType = dataTypeProp.Value.GetString()!;

            // 4. Flags (required, filterable, primary)
            bool required = false;
            if (fieldMap.TryGetValue("required", out var requiredProp))
            {
                if (requiredProp.Value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    return Result.Failure<EquipmentTypeSpecDefinition>(
                        CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Thuộc tính 'required' phải có kiểu boolean."));
                }
                required = requiredProp.Value.GetBoolean();
            }

            bool filterable = false;
            if (fieldMap.TryGetValue("filterable", out var filterableProp))
            {
                if (filterableProp.Value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    return Result.Failure<EquipmentTypeSpecDefinition>(
                        CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Thuộc tính 'filterable' phải có kiểu boolean."));
                }
                filterable = filterableProp.Value.GetBoolean();
            }

            bool primary = false;
            if (fieldMap.TryGetValue("primary", out var primaryProp))
            {
                if (primaryProp.Value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    return Result.Failure<EquipmentTypeSpecDefinition>(
                        CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Thuộc tính 'primary' phải có kiểu boolean."));
                }
                primary = primaryProp.Value.GetBoolean();
            }

            // 5. DisplayOrder
            int displayOrder = 0;
            if (fieldMap.TryGetValue("displayOrder", out var displayOrderProp))
            {
                if (displayOrderProp.Value.ValueKind != JsonValueKind.Number
                    || !displayOrderProp.Value.TryGetInt32(out displayOrder)
                    || displayOrder < 0)
                {
                    return Result.Failure<EquipmentTypeSpecDefinition>(
                        CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Thuộc tính 'displayOrder' phải là số nguyên không âm."));
                }
            }

            // 6. Optional text/metadata
            string? unit = null;
            if (fieldMap.TryGetValue("unit", out var unitProp))
            {
                if (unitProp.Value.ValueKind is not JsonValueKind.String and not JsonValueKind.Null)
                {
                    return Result.Failure<EquipmentTypeSpecDefinition>(
                        CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Thuộc tính 'unit' phải là chuỗi ký tự hoặc null."));
                }
                unit = unitProp.Value.ValueKind == JsonValueKind.Null ? null : unitProp.Value.GetString();
            }

            string? description = null;
            if (fieldMap.TryGetValue("description", out var descriptionProp))
            {
                if (descriptionProp.Value.ValueKind is not JsonValueKind.String and not JsonValueKind.Null)
                {
                    return Result.Failure<EquipmentTypeSpecDefinition>(
                        CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Thuộc tính 'description' phải là chuỗi ký tự hoặc null."));
                }
                description = descriptionProp.Value.ValueKind == JsonValueKind.Null ? null : descriptionProp.Value.GetString();
            }

            JsonElement? metadata = null;
            if (fieldMap.TryGetValue("metadata", out var metadataProp))
            {
                if (metadataProp.Value.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null)
                {
                    return Result.Failure<EquipmentTypeSpecDefinition>(
                        CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Thuộc tính 'metadata' phải là JSON object hoặc null."));
                }
                if (metadataProp.Value.ValueKind == JsonValueKind.Object)
                {
                    metadata = metadataProp.Value.Clone();
                }
            }

            // 7. Bounds (min/max)
            decimal? min = null;
            decimal? max = null;
            if (fieldMap.TryGetValue("min", out var minProp) && minProp.Value.ValueKind != JsonValueKind.Null)
            {
                if (minProp.Value.ValueKind != JsonValueKind.Number || !minProp.Value.TryGetDecimal(out decimal parsedMin))
                {
                    return Result.Failure<EquipmentTypeSpecDefinition>(
                        CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Thuộc tính 'min' phải là số hợp lệ."));
                }
                min = parsedMin;
            }

            if (fieldMap.TryGetValue("max", out var maxProp) && maxProp.Value.ValueKind != JsonValueKind.Null)
            {
                if (maxProp.Value.ValueKind != JsonValueKind.Number || !maxProp.Value.TryGetDecimal(out decimal parsedMax))
                {
                    return Result.Failure<EquipmentTypeSpecDefinition>(
                        CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Thuộc tính 'max' phải là số hợp lệ."));
                }
                max = parsedMax;
            }

            // 8. Options
            List<EquipmentTypeSpecOption>? options = null;
            if (fieldMap.TryGetValue("options", out var optionsProp) && optionsProp.Value.ValueKind != JsonValueKind.Null)
            {
                if (optionsProp.Value.ValueKind != JsonValueKind.Array)
                {
                    return Result.Failure<EquipmentTypeSpecDefinition>(
                        CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Thuộc tính 'options' phải là một mảng."));
                }

                options = new List<EquipmentTypeSpecOption>();
                foreach (var optElem in optionsProp.Value.EnumerateArray())
                {
                    if (optElem.ValueKind != JsonValueKind.Object)
                    {
                        return Result.Failure<EquipmentTypeSpecDefinition>(
                            CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Mỗi option phải là một JSON object."));
                    }

                    var optMap = new Dictionary<string, JsonProperty>(StringComparer.OrdinalIgnoreCase);
                    foreach (var optP in optElem.EnumerateObject())
                    {
                        if (!optMap.TryAdd(optP.Name, optP))
                        {
                            return Result.Failure<EquipmentTypeSpecDefinition>(
                                CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail(
                                    $"Thuộc tính '{optP.Name}' bị khai báo trùng lặp trong option."));
                        }
                    }

                    foreach (var optPName in optMap.Keys)
                    {
                        if (!AllowedOptionPropertyNames.Contains(optPName))
                        {
                            return Result.Failure<EquipmentTypeSpecDefinition>(
                                CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail(
                                    $"Thuộc tính '{optPName}' không thuộc contract của option."));
                        }
                    }

                    if (!optMap.TryGetValue("value", out var valProp) || valProp.Value.ValueKind != JsonValueKind.String)
                    {
                        return Result.Failure<EquipmentTypeSpecDefinition>(
                            CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Option 'value' bắt buộc và phải là chuỗi ký tự."));
                    }

                    if (!optMap.TryGetValue("label", out var optLabelProp) || optLabelProp.Value.ValueKind != JsonValueKind.String)
                    {
                        return Result.Failure<EquipmentTypeSpecDefinition>(
                            CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Option 'label' bắt buộc và phải là chuỗi ký tự."));
                    }

                    options.Add(new EquipmentTypeSpecOption
                    {
                        Value = valProp.Value.GetString()!,
                        Label = optLabelProp.Value.GetString()!
                    });
                }
            }

            fields.Add(new EquipmentTypeSpecField
            {
                Key = key,
                Label = label,
                DataType = dataType,
                Required = required,
                Filterable = filterable,
                Primary = primary,
                DisplayOrder = displayOrder,
                Min = min,
                Max = max,
                Options = options,
                Unit = unit,
                Description = description,
                Metadata = metadata
            });
        }

        var definition = new EquipmentTypeSpecDefinition
        {
            Fields = fields
        };

        var bizResult = Validate(definition);
        if (bizResult.IsFailure)
        {
            return Result.Failure<EquipmentTypeSpecDefinition>(bizResult.Error);
        }

        return Result.Success(definition);
    }

    public static Result Validate(EquipmentTypeSpecDefinition definition)
    {
        if (definition == null)
        {
            return Result.Failure(CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Definition không được null."));
        }

        if (definition.Fields == null)
        {
            return Result.Failure(CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Mảng 'fields' không được null."));
        }

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in definition.Fields)
        {
            if (field is null)
            {
                return Result.Failure(CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Phần tử trong 'fields' không được null."));
            }

            if (string.IsNullOrWhiteSpace(field.Key))
            {
                return Result.Failure(CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Field 'key' không được để trống."));
            }

            if (field.Key.Length != field.Key.Trim().Length)
            {
                return Result.Failure(CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Field 'key' không được chứa khoảng trắng ở đầu hoặc cuối."));
            }

            if (!seenKeys.Add(field.Key))
            {
                return Result.Failure(CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail($"Field 'key' '{field.Key}' bị trùng lặp trong schema."));
            }

            if (string.IsNullOrWhiteSpace(field.Label))
            {
                return Result.Failure(CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Field 'label' không được để trống."));
            }

            if (string.IsNullOrWhiteSpace(field.DataType) || !AllowedDataTypes.Contains(field.DataType))
            {
                return Result.Failure(CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail(
                    $"Kiểu dữ liệu '{field.DataType}' không được hỗ trợ. Chỉ hỗ trợ number, text, boolean, enum."));
            }

            if (field.DisplayOrder < 0)
            {
                return Result.Failure(CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Thuộc tính 'displayOrder' phải là số nguyên không âm."));
            }

            if (field.DataType is "number" or "integer")
            {
                if (field.Options != null)
                {
                    return Result.Failure(CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Kiểu dữ liệu 'number' không chấp nhận thuộc tính 'options'."));
                }

                if (field.Min.HasValue && field.Max.HasValue && field.Min.Value > field.Max.Value)
                {
                    return Result.Failure(CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Giá trị 'min' phải nhỏ hơn hoặc bằng 'max'."));
                }
            }
            else
            {
                if (field.Min.HasValue || field.Max.HasValue)
                {
                    return Result.Failure(CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail(
                        $"Kiểu dữ liệu '{field.DataType}' không chấp nhận ràng buộc 'min'/'max'."));
                }
            }

            if (field.DataType == "enum")
            {
                if (field.Options == null)
                {
                    return Result.Failure(CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Field kiểu 'enum' phải có mảng 'options'."));
                }

                if (field.Options.Count == 0)
                {
                    return Result.Failure(CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Field kiểu 'enum' phải có 'options' không rỗng."));
                }

                var seenOptionValues = new HashSet<string>(StringComparer.Ordinal);
                foreach (var opt in field.Options)
                {
                    if (opt is null)
                    {
                        return Result.Failure(CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Option trong 'options' không được null."));
                    }

                    if (string.IsNullOrWhiteSpace(opt.Value))
                    {
                        return Result.Failure(CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Option 'value' không được để trống."));
                    }

                    if (string.IsNullOrWhiteSpace(opt.Label))
                    {
                        return Result.Failure(CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail("Option 'label' không được để trống."));
                    }

                    if (!seenOptionValues.Add(opt.Value))
                    {
                        return Result.Failure(CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail(
                            $"Option value '{opt.Value}' bị trùng lặp trong field '{field.Key}'."));
                    }
                }
            }
            else
            {
                if (field.Options != null)
                {
                    return Result.Failure(CatalogErrors.EquipmentType.InvalidSchemaDefinitionWithDetail(
                        $"Kiểu dữ liệu '{field.DataType}' không chấp nhận thuộc tính 'options'."));
                }
            }
        }

        return Result.Success();
    }
}
