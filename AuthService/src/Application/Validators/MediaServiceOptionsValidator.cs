using Dprental.AuthService.Application.Options;
using Microsoft.Extensions.Options;

namespace Dprental.AuthService.Application.Validators;

/// <summary>Kiểm tra tính hợp lệ của cấu hình MediaService khi ứng dụng khởi động.</summary>
public sealed class MediaServiceOptionsValidator : IValidateOptions<MediaServiceOptions>
{
    public ValidateOptionsResult Validate(string? name, MediaServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.InternalBaseUrl))
        {
            failures.Add("MediaService:InternalBaseUrl must be configured.");
        }
        else if (!Uri.TryCreate(options.InternalBaseUrl, UriKind.Absolute, out Uri? internalUri) ||
                 (internalUri.Scheme != Uri.UriSchemeHttp && internalUri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add("MediaService:InternalBaseUrl must be a valid absolute HTTP/HTTPS URL.");
        }

        if (string.IsNullOrWhiteSpace(options.PublicGatewayUrl))
        {
            failures.Add("MediaService:PublicGatewayUrl must be configured.");
        }
        else if (!Uri.TryCreate(options.PublicGatewayUrl, UriKind.Absolute, out Uri? publicUri) ||
                 (publicUri.Scheme != Uri.UriSchemeHttp && publicUri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add("MediaService:PublicGatewayUrl must be a valid absolute HTTP/HTTPS URL.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            failures.Add("MediaService:ApiKey must be configured.");
        }

        if (options.TimeoutSeconds is < 1 or > 30)
        {
            failures.Add("MediaService:TimeoutSeconds must be between 1 and 30.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
