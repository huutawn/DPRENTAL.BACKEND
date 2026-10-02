using System.Collections;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Dprental.CatalogService.Api.Filters;

public sealed class UpdateEquipmentTypeRequestSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type != typeof(UpdateEquipmentTypeRequest))
        {
            return;
        }

        var propertiesProp = schema.GetType().GetProperty("Properties");
        if (propertiesProp?.GetValue(schema) is IDictionary properties)
        {
            var allowedProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "code", "name", "description", "categoryIds", "media"
            };

            var toRemove = new List<object>();
            foreach (var key in properties.Keys)
            {
                if (key is string s && !allowedProps.Contains(s))
                {
                    toRemove.Add(key);
                }
            }

            foreach (var key in toRemove)
            {
                properties.Remove(key);
            }

            foreach (var key in properties.Keys)
            {
                var val = properties[key];
                if (val is Microsoft.OpenApi.OpenApiSchema s)
                {
                    bool isDescription = string.Equals(key as string, "description", StringComparison.OrdinalIgnoreCase);
                    if (isDescription)
                    {
                        if (s.Type.HasValue)
                        {
                            s.Type = s.Type.Value | JsonSchemaType.Null;
                        }
                    }
                    else
                    {
                        if (s.Type.HasValue)
                        {
                            s.Type = s.Type.Value & ~JsonSchemaType.Null;
                        }
                    }
                }
            }
        }

        var requiredProp = schema.GetType().GetProperty("Required");
        if (requiredProp?.GetValue(schema) is IList requiredList)
        {
            requiredList.Clear();
        }
    }
}
