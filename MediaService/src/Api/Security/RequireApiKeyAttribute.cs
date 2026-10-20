using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.MediaService.Api.Security;

/// <summary>
/// Đánh dấu endpoint nội bộ yêu cầu API key. Kiểm tra chính được thực hiện tập trung tại InternalApiKeyMiddleware.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireApiKeyAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // Đã được xác thực qua InternalApiKeyMiddleware, bỏ kiểm tra trùng lặp tại filter.
        await next();
    }
}
