using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Dprental.CatalogService.Api.Filters;

/// <summary>
/// Gắn yêu cầu Bearer vào các operation cần đăng nhập để Swagger UI gửi token sau khi Authorize.
/// Fallback policy yêu cầu xác thực cho mọi endpoint, nên chỉ bỏ qua endpoint có <see cref="IAllowAnonymous"/>.
/// </summary>
public sealed class BearerSecurityRequirementOperationFilter : IOperationFilter
{
    /// <summary>Tên scheme do <c>InferSecuritySchemes</c> sinh ra từ JWT Bearer authentication.</summary>
    public const string SchemeName = "Bearer";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        if (context.ApiDescription.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
        {
            return;
        }

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(SchemeName, context.Document)] = []
        });
    }
}
