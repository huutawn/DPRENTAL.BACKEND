using Dprental.Gateway.Domain.Security;
using Microsoft.AspNetCore.Http;

namespace Dprental.Gateway.Application.Security;

/// <summary>
/// Cung cấp cơ chế lưu trữ và truy xuất ngữ cảnh danh tính thông qua HttpContext.Items
/// và AsyncLocal làm phương án dự phòng.
/// </summary>
public sealed class IdentityContextAccessor : IIdentityContextAccessor
{
    private const string HttpContextItemKey = "Dprental.Gateway.IdentityContext";
    private readonly IHttpContextAccessor httpContextAccessor;
    private static readonly AsyncLocal<IdentityContext?> AsyncLocalContext = new();

    /// <summary>
    /// Khởi tạo accessor với HttpContextAccessor được tiêm phụ thuộc.
    /// </summary>
    /// <param name="httpContextAccessor">Bộ truy xuất HttpContext hiện tại.</param>
    public IdentityContextAccessor(IHttpContextAccessor httpContextAccessor)
    {
        this.httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    }

    /// <inheritdoc />
    public IdentityContext Current
    {
        get
        {
            HttpContext? httpContext = httpContextAccessor.HttpContext;
            if (httpContext is not null && httpContext.Items.TryGetValue(HttpContextItemKey, out object? item) && item is IdentityContext ctx)
            {
                return ctx;
            }

            return AsyncLocalContext.Value ?? IdentityContext.Anonymous;
        }
    }

    /// <inheritdoc />
    public void SetCurrent(IdentityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        HttpContext? httpContext = httpContextAccessor.HttpContext;
        if (httpContext is not null)
        {
            httpContext.Items[HttpContextItemKey] = context;
        }

        AsyncLocalContext.Value = context;
    }
}
