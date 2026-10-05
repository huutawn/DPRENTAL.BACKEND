using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dprental.CatalogService.Application.DTOs.Brands;

[JsonConverter(typeof(UpdateBrandRequestJsonConverter))]
public sealed class UpdateBrandRequest
{
    [JsonIgnore]
    public bool IsCodeSpecified { get; init; }
    public string? Code { get; init; }

    [JsonIgnore]
    public bool IsNameSpecified { get; init; }
    public string? Name { get; init; }

    [JsonIgnore]
    public bool IsSlugSpecified { get; init; }
    public string? Slug { get; init; }

    [JsonIgnore]
    public bool IsLogoMediaIdSpecified { get; init; }
    public Guid? LogoMediaId { get; init; }

    [JsonIgnore]
    public bool HasAnyFieldSpecified =>
        IsCodeSpecified || IsNameSpecified || IsSlugSpecified || IsLogoMediaIdSpecified;
}

public sealed class UpdateBrandRequestJsonConverter : JsonConverter<UpdateBrandRequest>
{
    public override UpdateBrandRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected start of object.");
        }

        bool isCodeSpecified = false;
        string? code = null;
        bool isNameSpecified = false;
        string? name = null;
        bool isSlugSpecified = false;
        string? slug = null;
        bool isLogoMediaIdSpecified = false;
        Guid? logoMediaId = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new UpdateBrandRequest
                {
                    IsCodeSpecified = isCodeSpecified,
                    Code = code,
                    IsNameSpecified = isNameSpecified,
                    Name = name,
                    IsSlugSpecified = isSlugSpecified,
                    Slug = slug,
                    IsLogoMediaIdSpecified = isLogoMediaIdSpecified,
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
                isCodeSpecified = true;
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
                    throw new JsonException("Code must be a string or null.");
                }
            }
            else if (string.Equals(propName, "name", StringComparison.OrdinalIgnoreCase))
            {
                isNameSpecified = true;
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
                    throw new JsonException("Name must be a string or null.");
                }
            }
            else if (string.Equals(propName, "slug", StringComparison.OrdinalIgnoreCase))
            {
                isSlugSpecified = true;
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
                    throw new JsonException("Slug must be a string or null.");
                }
            }
            else if (string.Equals(propName, "logoMediaId", StringComparison.OrdinalIgnoreCase))
            {
                isLogoMediaIdSpecified = true;
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

    public override void Write(Utf8JsonWriter writer, UpdateBrandRequest value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        if (value.IsCodeSpecified)
        {
            if (value.Code is null) writer.WriteNull("code");
            else writer.WriteString("code", value.Code);
        }
        if (value.IsNameSpecified)
        {
            if (value.Name is null) writer.WriteNull("name");
            else writer.WriteString("name", value.Name);
        }
        if (value.IsSlugSpecified)
        {
            if (value.Slug is null) writer.WriteNull("slug");
            else writer.WriteString("slug", value.Slug);
        }
        if (value.IsLogoMediaIdSpecified)
        {
            if (value.LogoMediaId is null) writer.WriteNull("logoMediaId");
            else writer.WriteString("logoMediaId", value.LogoMediaId.Value);
        }
        writer.WriteEndObject();
    }
}
