using Dprental.MediaService.Api.Controllers;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Dprental.MediaService.Api.Security;

/// <summary>Document the Gateway credential for internal media operations in Swagger UI.</summary>
public sealed class ApiKeyOperationFilter : IOperationFilter
{
    private static readonly HashSet<string> InternalActions =
    [
        nameof(MediaController.VerifyMedia),
        nameof(MediaController.GetMetadata),
        nameof(MediaController.CreatePrivateAccess),
        nameof(MediaController.SetVisibility)
    ];

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.MethodInfo.DeclaringType != typeof(MediaController) ||
            !InternalActions.Contains(context.MethodInfo.Name))
        {
            return;
        }

        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "X-Api-Key",
            In = ParameterLocation.Header,
            Required = true,
            Description = "Shared service API key cho API nội bộ MediaService."
        });
        operation.Security?.Clear();
    }
}
