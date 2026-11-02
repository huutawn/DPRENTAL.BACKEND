using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace Dprental.RentalService.Api.Security;

/// <remarks>
/// Handler của HTTP client Auth: giữ Authorization đã có; nếu chưa có thì lấy Bearer từ request hiện tại
/// hoặc token do authentication lưu để truyền tiếp. Không tự cấp token hoặc quyết định quyền nghiệp vụ.
/// </remarks>
public sealed class AuthTokenPropagationHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AuthTokenPropagationHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is null)
        {
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext is not null)
            {
                string? token = ExtractBearerToken(httpContext);
                if (string.IsNullOrWhiteSpace(token))
                {
                    try
                    {
                        token = await httpContext.GetTokenAsync("access_token");
                    }
                    catch
                    {
                        // Không lấy thêm được token khi authentication provider chưa được cấu hình; tiếp tục request hiện tại.
                    }
                }

                if (!string.IsNullOrWhiteSpace(token))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private static string? ExtractBearerToken(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("Authorization", out var authHeaderValues))
        {
            string authHeader = authHeaderValues.ToString();
            if (AuthenticationHeaderValue.TryParse(authHeader, out var parsed) &&
                string.Equals(parsed.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(parsed.Parameter))
            {
                return parsed.Parameter;
            }

            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return authHeader["Bearer ".Length..].Trim();
            }
        }

        return null;
    }
}
