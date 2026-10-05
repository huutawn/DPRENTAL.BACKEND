using Microsoft.Extensions.Options;

namespace Dprental.CatalogService.Application.Options;

/// <summary>
/// Xác thực cấu hình JWT khi khởi động ứng dụng.
/// </summary>
public sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            failures.Add("Auth:Jwt:Issuer không được để trống.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            failures.Add("Auth:Jwt:Audience không được để trống.");
        }

        if (!Uri.TryCreate(options.JwksUri, UriKind.Absolute, out Uri? jwksUri)
            || (jwksUri.Scheme != Uri.UriSchemeHttp && jwksUri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add("Auth:Jwt:JwksUri phải là URL HTTP hoặc HTTPS tuyệt đối hợp lệ.");
        }

        if (options.TimeoutSeconds <= 0)
        {
            failures.Add("Auth:Jwt:TimeoutSeconds phải lớn hơn 0.");
        }

        if (options.CacheDurationMinutes <= 0)
        {
            failures.Add("Auth:Jwt:CacheDurationMinutes phải lớn hơn 0.");
        }

        if (options.MaxStaleMinutes < options.CacheDurationMinutes)
        {
            failures.Add("Auth:Jwt:MaxStaleMinutes không được nhỏ hơn CacheDurationMinutes.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
