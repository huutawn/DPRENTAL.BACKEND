using System.Text.Json.Nodes;
using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.DTOs.EquipmentTypes;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Dprental.CatalogService.Api.Filters;

/// <summary>
/// Khai báo bộ lọc <c>status</c> của danh sách quản trị là một giá trị enum để Swagger UI hiện dropdown.
/// Chỉ đổi tài liệu OpenAPI: request vẫn bind danh sách chuỗi và được kiểm tra ở validator như cũ,
/// nên gọi trực tiếp vẫn lặp lại được tham số để lấy nhiều trạng thái.
/// </summary>
public sealed class EquipmentTypeStatusParameterFilter : IParameterFilter
{
    public void Apply(IOpenApiParameter parameter, ParameterFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(context);

        if (context.PropertyInfo?.DeclaringType != typeof(GetAdminEquipmentTypesRequest)
            || context.PropertyInfo.Name != nameof(GetAdminEquipmentTypesRequest.Status)
            || parameter is not OpenApiParameter statusParameter)
        {
            return;
        }

        statusParameter.Schema = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Enum = [.. Enum.GetNames<EquipmentTypeStatus>().Select(name => (JsonNode)JsonValue.Create(name))]
        };
    }
}
