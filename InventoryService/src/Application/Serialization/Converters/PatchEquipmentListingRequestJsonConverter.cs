using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Common.Enums;

namespace Dprental.InventoryService.Application.Serialization.Converters;

/// <summary>
/// Custom JsonConverter cho PatchEquipmentListingRequest thực thi presence tracking,
/// từ chối field 'quantity' và các trường không hợp lệ, đồng thời kiểm tra kiểu dữ liệu nghiêm ngặt.
/// </summary>
public sealed class PatchEquipmentListingRequestJsonConverter : JsonConverter<PatchEquipmentListingRequest>
{
    private static readonly HashSet<string> AllowedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "brandId",
        "name",
        "description",
        "model",
        "thumbnailMediaId",
        "schemaVersion",
        "specValues",
        "status"
    };
    public override PatchEquipmentListingRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Nội dung yêu cầu PATCH phải là một JSON object.");
        }

        var request = new PatchEquipmentListingRequest();
        var seenProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return request;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Mong đợi tên thuộc tính JSON.");
            }

            string propertyName = reader.GetString()!;

            if (!seenProperties.Add(propertyName))
            {
                throw new JsonException($"Thuộc tính '{propertyName}' bị lặp lại trong nội dung yêu cầu.");
            }

            if (string.Equals(propertyName, "quantity", StringComparison.OrdinalIgnoreCase))
            {
                throw new JsonException("Thuộc tính 'quantity' không được phép cập nhật qua PATCH. Vui lòng sử dụng endpoint thêm đơn vị thiết bị.");
            }

            if (!AllowedProperties.Contains(propertyName))
            {
                throw new JsonException($"Thuộc tính '{propertyName}' không được hỗ trợ trong yêu cầu PATCH EquipmentListing.");
            }

            reader.Read();

            switch (propertyName.ToLowerInvariant())
            {
                case "brandid":
                    request.IsBrandIdSpecified = true;
                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        request.BrandId = null;
                    }
                    else if (reader.TokenType == JsonTokenType.String && reader.TryGetGuid(out var brandGuid))
                    {
                        request.BrandId = brandGuid;
                    }
                    else
                    {
                        throw new JsonException("Thuộc tính 'brandId' phải là chuỗi GUID hợp lệ hoặc null.");
                    }
                    break;
                case "name":
                    request.IsNameSpecified = true;
                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        request.Name = null;
                    }
                    else if (reader.TokenType == JsonTokenType.String)
                    {
                        request.Name = reader.GetString();
                    }
                    else
                    {
                        throw new JsonException("Thuộc tính 'name' phải là kiểu chuỗi hoặc null.");
                    }
                    break;

                case "description":
                    request.IsDescriptionSpecified = true;
                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        request.Description = null;
                    }
                    else if (reader.TokenType == JsonTokenType.String)
                    {
                        request.Description = reader.GetString();
                    }
                    else
                    {
                        throw new JsonException("Thuộc tính 'description' phải là kiểu chuỗi hoặc null.");
                    }
                    break;

                case "model":
                    request.IsModelSpecified = true;
                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        request.Model = null;
                    }
                    else if (reader.TokenType == JsonTokenType.String)
                    {
                        request.Model = reader.GetString();
                    }
                    else
                    {
                        throw new JsonException("Thuộc tính 'model' phải là kiểu chuỗi.");
                    }
                    break;

                case "thumbnailmediaid":
                    request.IsThumbnailMediaIdSpecified = true;
                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        request.ThumbnailMediaId = null;
                    }
                    else if (reader.TokenType == JsonTokenType.String && reader.TryGetGuid(out var thumbGuid))
                    {
                        request.ThumbnailMediaId = thumbGuid;
                    }
                    else
                    {
                        throw new JsonException("Thuộc tính 'thumbnailMediaId' phải là chuỗi GUID hợp lệ hoặc null.");
                    }
                    break;

                case "schemaversion":
                    request.IsSchemaVersionSpecified = true;
                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        request.SchemaVersion = null;
                    }
                    else if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var sVersion))
                    {
                        request.SchemaVersion = sVersion;
                    }
                    else
                    {
                        throw new JsonException("Thuộc tính 'schemaVersion' phải là số nguyên hoặc null.");
                    }
                    break;

                case "specvalues":
                    request.IsSpecValuesSpecified = true;
                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        request.SpecValues = null;
                    }
                    else if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        request.SpecValues = JsonElement.ParseValue(ref reader);
                    }
                    else
                    {
                        throw new JsonException("Thuộc tính 'specValues' phải là JSON object hoặc null.");
                    }
                    break;

                case "status":
                    request.IsStatusSpecified = true;
                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        request.Status = null;
                    }
                    else if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var statusVal))
                    {
                        if (Enum.IsDefined(typeof(EquipmentListingStatus), statusVal))
                        {
                            request.Status = (EquipmentListingStatus)statusVal;
                        }
                        else
                        {
                            throw new JsonException($"Giá trị trạng thái '{statusVal}' không hợp lệ.");
                        }
                    }
                    else if (reader.TokenType == JsonTokenType.String)
                    {
                        var statusStr = reader.GetString();
                        if (!int.TryParse(statusStr, out _) &&
                            Enum.TryParse<EquipmentListingStatus>(statusStr, ignoreCase: true, out var parsedStatus) &&
                            Enum.IsDefined(parsedStatus))
                        {
                            request.Status = parsedStatus;
                        }
                        else
                        {
                            throw new JsonException($"Giá trị trạng thái '{statusStr}' không hợp lệ.");
                        }
                    }
                    else
                    {
                        throw new JsonException("Thuộc tính 'status' phải là số nguyên hoặc chuỗi trạng thái hợp lệ.");
                    }
                    break;
            }
        }

        return request;
    }

    public override void Write(Utf8JsonWriter writer, PatchEquipmentListingRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (value.IsBrandIdSpecified)
        {
            if (value.BrandId == null)
            {
                writer.WriteNull("brandId");
            }
            else
            {
                writer.WriteString("brandId", value.BrandId.Value);
            }
        }
        if (value.IsNameSpecified)
        {
            if (value.Name == null)
            {
                writer.WriteNull("name");
            }
            else
            {
                writer.WriteString("name", value.Name);
            }
        }

        if (value.IsDescriptionSpecified)
        {
            if (value.Description == null)
            {
                writer.WriteNull("description");
            }
            else
            {
                writer.WriteString("description", value.Description);
            }
        }

        if (value.IsModelSpecified)
        {
            writer.WriteString("model", value.Model);
        }

        if (value.IsThumbnailMediaIdSpecified)
        {
            if (value.ThumbnailMediaId == null)
            {
                writer.WriteNull("thumbnailMediaId");
            }
            else
            {
                writer.WriteString("thumbnailMediaId", value.ThumbnailMediaId.Value);
            }
        }

        if (value.IsSchemaVersionSpecified)
        {
            if (value.SchemaVersion == null)
            {
                writer.WriteNull("schemaVersion");
            }
            else
            {
                writer.WriteNumber("schemaVersion", value.SchemaVersion.Value);
            }
        }

        if (value.IsSpecValuesSpecified)
        {
            if (value.SpecValues == null)
            {
                writer.WriteNull("specValues");
            }
            else
            {
                writer.WritePropertyName("specValues");
                value.SpecValues.Value.WriteTo(writer);
            }
        }

        if (value.IsStatusSpecified)
        {
            if (value.Status == null)
            {
                writer.WriteNull("status");
            }
            else
            {
                writer.WriteString("status", value.Status.Value.ToString());
            }
        }

        writer.WriteEndObject();
    }
}
