using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dprental.CatalogService.Application.DTOs.Brands;

[JsonConverter(typeof(CreateBrandRequestJsonConverter))]
public sealed class CreateBrandRequest
{
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public Guid? LogoMediaId { get; init; }

    public CreateBrandRequest()
    {
    }

    public CreateBrandRequest(string code, string name, string slug, Guid? logoMediaId = null)
    {
        Code = code;
        Name = name;
        Slug = slug;
        LogoMediaId = logoMediaId;
    }
}

public sealed class CreateBrandRequestJsonConverter : JsonConverter<CreateBrandRequest>
{
    public override CreateBrandRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected start of object.");
        }

        string? code = null;
        string? name = null;
        string? slug = null;
        Guid? logoMediaId = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new CreateBrandRequest
                {
                    Code = code ?? string.Empty,
                    Name = name ?? string.Empty,
                    Slug = slug ?? string.Empty,
                    LogoMediaId = logoMediaId
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
                if (reader.TokenType == JsonTokenType.Null)
                {
                    code = null;
                }
                else if (reader.TokenType == JsonTokenType.String)
                {
                    code = reader.GetString();
                }
                else
                {
                    throw new JsonException("Code must be a string.");
                }
            }
            else if (string.Equals(propName, "name", StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType == JsonTokenType.Null)
                {
                    name = null;
                }
                else if (reader.TokenType == JsonTokenType.String)
                {
                    name = reader.GetString();
                }
                else
                {
                    throw new JsonException("Name must be a string.");
                }
            }
            else if (string.Equals(propName, "slug", StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType == JsonTokenType.Null)
                {
                    slug = null;
                }
                else if (reader.TokenType == JsonTokenType.String)
                {
                    slug = reader.GetString();
                }
                else
                {
                    throw new JsonException("Slug must be a string.");
                }
            }
            else if (string.Equals(propName, "logoMediaId", StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType == JsonTokenType.Null)
                {
                    logoMediaId = null;
                }
                else if (reader.TokenType == JsonTokenType.String && Guid.TryParse(reader.GetString(), out var g))
                {
                    logoMediaId = g;
                }
                else
                {
                    throw new JsonException("LogoMediaId must be a valid GUID string or null.");
                }
            }
            else
            {
                throw new JsonException($"Field '{propName}' is not allowed in request contract.");
            }
        }

        throw new JsonException("Unexpected end of JSON input.");
    }

    public override void Write(Utf8JsonWriter writer, CreateBrandRequest value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WriteString("code", value.Code);
        writer.WriteString("name", value.Name);
        writer.WriteString("slug", value.Slug);
        if (value.LogoMediaId.HasValue)
        {
            writer.WriteString("logoMediaId", value.LogoMediaId.Value);
        }
        else
        {
            writer.WriteNull("logoMediaId");
        }
        writer.WriteEndObject();
    }
}
