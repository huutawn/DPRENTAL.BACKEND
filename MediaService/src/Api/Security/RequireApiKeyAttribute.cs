using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.MediaService.Api.Security;

/// <summary>
/// Bộ lọc xác thực yêu cầu header X-Api-Key hợp lệ trên các endpoint nội bộ.
/// Trả về 401 Unauthorized khi thiếu hoặc sai key.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireApiKeyAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var validator = context.HttpContext.RequestServices.GetRequiredService<IApiKeyValidator>();
        if (!context.HttpContext.Request.Headers.TryGetValue("X-Api-Key", out var headerValues) ||
            headerValues.Count != 1 ||
            !validator.IsValid(headerValues[0]))
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Unauthorized",
                Detail = "API key không hợp lệ hoặc bị thiếu."
            })
            {
                StatusCode = StatusCodes.Status401Unauthorized
            };
            return;
        }

        await next();
    }
}
