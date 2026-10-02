using Microsoft.Extensions.Options;

namespace Dprental.CatalogService.Application.Options;

/// <summary>
/// Xác thực cấu hình MediaService khi khởi động ứng dụng.
/// </summary>
public sealed class MediaServiceOptionsValidator : IValidateOptions<MediaServiceOptions>
{
    private readonly bool _isProduction;

    public MediaServiceOptionsValidator(bool isProduction = false)
    {
        _isProduction = isProduction;
    }

    public ValidateOptionsResult Validate(string? name, MediaServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.InternalBaseUrl))
        {
            failures.Add("MediaService:InternalBaseUrl không được để trống.");
        }
        else if (!Uri.TryCreate(options.InternalBaseUrl, UriKind.Absolute, out Uri? internalUri)
            || (internalUri.Scheme != Uri.UriSchemeHttp && internalUri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add("MediaService:InternalBaseUrl phải là URL HTTP hoặc HTTPS tuyệt đối hợp lệ.");
        }

        if (string.IsNullOrWhiteSpace(options.PublicGatewayUrl))
        {
            failures.Add("MediaService:PublicGatewayUrl không được để trống.");
        }
        else if (!Uri.TryCreate(options.PublicGatewayUrl, UriKind.Absolute, out Uri? gatewayUri)
            || (gatewayUri.Scheme != Uri.UriSchemeHttp && gatewayUri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add("MediaService:PublicGatewayUrl phải là URL HTTP hoặc HTTPS tuyệt đối hợp lệ.");
        }
        else if (_isProduction && gatewayUri.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add("MediaService:PublicGatewayUrl phải dùng HTTPS trong môi trường Production.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            failures.Add("MediaService:ApiKey không được để trống.");
        }

        if (options.TimeoutSeconds <= 0)
        {
            failures.Add("MediaService:TimeoutSeconds phải lớn hơn 0.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
