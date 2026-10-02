using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dprental.CatalogService.Domain.DTOs.EquipmentTypes;

[JsonConverter(typeof(CreateEquipmentTypeRequestJsonConverter))]
public sealed class CreateEquipmentTypeRequest
{
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public IReadOnlyList<Guid> CategoryIds { get; init; } = [];
    public IReadOnlyList<EquipmentTypeMediaItemDto?>? Media { get; init; } = [];

    /// <summary>
    /// Organization sẽ sở hữu equipment type. Chỉ là lựa chọn trong các organization của caller:
    /// service luôn đối chiếu với claim <c>o</c>, không tin giá trị này một mình.
    /// </summary>
    public Guid? OrganizationId { get; init; }

    [JsonIgnore]
    public bool IsMediaSpecified { get; init; }

    public CreateEquipmentTypeRequest()
    {
    }

    public CreateEquipmentTypeRequest(
        string code,
        string name,
        string? description,
        IReadOnlyList<Guid> categoryIds,
        IReadOnlyList<EquipmentTypeMediaItemDto?>? media,
        bool isMediaSpecified = true)
    {
        Code = code;
        Name = name;
        Description = description;
        CategoryIds = categoryIds ?? [];
        Media = media ?? [];
        IsMediaSpecified = isMediaSpecified || media != null;
    }
}

public sealed class CreateEquipmentTypeRequestJsonConverter : JsonConverter<CreateEquipmentTypeRequest>
{
    public override CreateEquipmentTypeRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected start of object.");
        }

        string code = string.Empty;
        string name = string.Empty;
        string? description = null;
        IReadOnlyList<Guid> categoryIds = [];
        bool isMediaSpecified = false;
        IReadOnlyList<EquipmentTypeMediaItemDto?>? media = [];
        Guid? organizationId = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new CreateEquipmentTypeRequest
                {
                    Code = code,
                    Name = name,
                    Description = description,
                    CategoryIds = categoryIds,
                    Media = media,
                    OrganizationId = organizationId,
                    IsMediaSpecified = isMediaSpecified
                };
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected property name.");
            }

            string propName = reader.GetString() ?? string.Empty;
            reader.Read();

            if (string.Equals(propName, "code", StringComparison.OrdinalIgnoreCase))
            {
                code = reader.TokenType == JsonTokenType.Null ? null! : reader.GetString() ?? string.Empty;
            }
            else if (string.Equals(propName, "name", StringComparison.OrdinalIgnoreCase))
            {
                name = reader.TokenType == JsonTokenType.Null ? null! : reader.GetString() ?? string.Empty;
            }
            else if (string.Equals(propName, "description", StringComparison.OrdinalIgnoreCase))
            {
                description = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
            }
            else if (string.Equals(propName, "categoryIds", StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType == JsonTokenType.Null)
                {
                    categoryIds = null!;
                }
                else if (reader.TokenType == JsonTokenType.StartArray)
                {
                    var list = new List<Guid>();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType == JsonTokenType.String && Guid.TryParse(reader.GetString(), out var g))
                        {
                            list.Add(g);
                        }
                        else
                        {
                            throw new JsonException("CategoryIds elements must be valid GUID strings.");
                        }
                    }
                    categoryIds = list;
                }
                else
                {
                    throw new JsonException("CategoryIds must be an array.");
                }
            }
            else if (string.Equals(propName, "organizationId", StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType == JsonTokenType.Null)
                {
                    organizationId = null;
                }
                else if (reader.TokenType == JsonTokenType.String && Guid.TryParse(reader.GetString(), out var parsedOrganizationId))
                {
                    organizationId = parsedOrganizationId;
                }
                else
                {
                    throw new JsonException("OrganizationId must be a valid GUID string.");
                }
            }
            else if (string.Equals(propName, "media", StringComparison.OrdinalIgnoreCase))
            {
                isMediaSpecified = true;
                if (reader.TokenType == JsonTokenType.Null)
                {
                    media = null;
                }
                else if (reader.TokenType == JsonTokenType.StartArray)
                {
                    media = JsonSerializer.Deserialize<IReadOnlyList<EquipmentTypeMediaItemDto?>>(ref reader, options);
                }
                else
                {
                    throw new JsonException("Media must be an array or null.");
                }
            }
            else
            {
                throw new JsonException($"Field '{propName}' is not allowed in request contract.");
            }
        }

        throw new JsonException("Unexpected end of JSON input.");
    }

    public override void Write(Utf8JsonWriter writer, CreateEquipmentTypeRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("code", value.Code);
        writer.WriteString("name", value.Name);
        if (value.Description != null)
        {
            writer.WriteString("description", value.Description);
        }
        else
        {
            writer.WriteNull("description");
        }

        if (value.CategoryIds != null)
        {
            writer.WritePropertyName("categoryIds");
            writer.WriteStartArray();
            foreach (var id in value.CategoryIds)
            {
                writer.WriteStringValue(id);
            }
            writer.WriteEndArray();
        }
        else
        {
            writer.WriteNull("categoryIds");
        }

        if (value.IsMediaSpecified || value.Media != null)
        {
            writer.WritePropertyName("media");
            JsonSerializer.Serialize(writer, value.Media, options);
        }

        if (value.OrganizationId.HasValue)
        {
            writer.WriteString("organizationId", value.OrganizationId.Value);
        }
        writer.WriteEndObject();
    }
}
