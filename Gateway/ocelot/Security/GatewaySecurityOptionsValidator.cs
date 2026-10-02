using Microsoft.Extensions.Options;

namespace Dprental.Gateway.Security;

/// <summary>
/// Kiểm tra tính hợp lệ của toàn bộ cấu hình bảo mật API Gateway trước khi ứng dụng khởi động.
/// </summary>
public sealed class GatewaySecurityOptionsValidator : IValidateOptions<GatewaySecurityOptions>
{
    /// <summary>
    /// Thực hiện thẩm định các ràng buộc cấu hình bảo mật.
    /// </summary>
    /// <param name="name">Tên của tùy chọn options đang kiểm tra.</param>
    /// <param name="options">Đối tượng cấu hình bảo mật.</param>
    /// <returns>Kết quả thẩm định (Success hoặc Fail với thông điệp lỗi cụ thể).</returns>
    public ValidateOptionsResult Validate(string? name, GatewaySecurityOptions? options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("Cấu hình Gateway:Security không được để trống.");
        }

        List<string> failures = new();

        // 1. Kiểm tra cấu hình Request Size Protection
        if (options.RequestSize.MaxRequestBodySizeBytes <= 0)
        {
            failures.Add("Gateway:Security:RequestSize:MaxRequestBodySizeBytes phải lớn hơn 0.");
        }

        if (options.RequestSize.MediaUploadMaxRequestBodySizeBytes <= 0)
        {
            failures.Add("Gateway:Security:RequestSize:MediaUploadMaxRequestBodySizeBytes phải lớn hơn 0.");
        }

        // 2. Kiểm tra cấu hình CORS
        if (options.Cors.PreflightMaxAgeSeconds < 0)
        {
            failures.Add("Gateway:Security:Cors:PreflightMaxAgeSeconds không được là số âm.");
        }

        if (options.Cors.AllowCredentials && options.Cors.AllowedOrigins.Contains("*", StringComparer.Ordinal))
        {
            failures.Add("Theo đặc tả bảo mật CORS, không được phép kết hợp AllowCredentials=true với AllowedOrigins='*' (wildcard).");
        }

        foreach (string origin in options.Cors.AllowedOrigins)
        {
            if (string.IsNullOrWhiteSpace(origin))
            {
                failures.Add("Danh sách AllowedOrigins không được chứa giá trị rỗng hoặc chỉ có khoảng trắng.");
                continue;
            }

            if (origin != "*" && (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? parsedUri) || (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps)))
            {
                failures.Add($"AllowedOrigin '{origin}' không phải là một URI hợp lệ (phải bắt đầu bằng http:// hoặc https://).");
            }
        }

        // 3. Kiểm tra cấu hình Security Headers
        if (options.Headers.HstsMaxAgeSeconds < 0)
        {
            failures.Add("Gateway:Security:Headers:HstsMaxAgeSeconds không được là số âm.");
        }

        if (string.IsNullOrWhiteSpace(options.Identity.ServiceName))
        {
            failures.Add("Gateway:Security:Identity:ServiceName không được để trống.");
        }

        // 4. Kiểm tra cấu hình Rate Limiting
        if (options.RateLimiting.WindowSeconds <= 0)
        {
            failures.Add("Gateway:Security:RateLimiting:WindowSeconds phải lớn hơn 0.");
        }

        if (options.RateLimiting.SegmentsPerWindow <= 0)
        {
            failures.Add("Gateway:Security:RateLimiting:SegmentsPerWindow phải lớn hơn 0.");
        }

        if (options.RateLimiting.UserReadLimit <= 0)
        {
            failures.Add("Gateway:Security:RateLimiting:UserReadLimit phải lớn hơn 0.");
        }

        if (options.RateLimiting.UserWriteLimit <= 0)
        {
            failures.Add("Gateway:Security:RateLimiting:UserWriteLimit phải lớn hơn 0.");
        }

        if (options.RateLimiting.IpReadLimit <= 0)
        {
            failures.Add("Gateway:Security:RateLimiting:IpReadLimit phải lớn hơn 0.");
        }

        if (options.RateLimiting.IpWriteLimit <= 0)
        {
            failures.Add("Gateway:Security:RateLimiting:IpWriteLimit phải lớn hơn 0.");
        }

        if (options.RateLimiting.WindowSeconds > 0 &&
            options.RateLimiting.SegmentsPerWindow > 0 &&
            options.RateLimiting.WindowSeconds % options.RateLimiting.SegmentsPerWindow != 0)
        {
            failures.Add("Gateway:Security:RateLimiting:WindowSeconds phải chia hết cho SegmentsPerWindow.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
