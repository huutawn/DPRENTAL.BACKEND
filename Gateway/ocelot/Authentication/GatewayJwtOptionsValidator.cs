using Microsoft.Extensions.Options;

namespace Dprental.Gateway.Authentication;

/// <summary>Từ chối cấu hình JWT thiếu hoặc sai định dạng ngay khi khởi động Gateway.</summary>
internal sealed class GatewayJwtOptionsValidator : IValidateOptions<GatewayJwtOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, GatewayJwtOptions options)
    {
        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            failures.Add("Gateway:Authentication:Jwt:Issuer must be configured.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            failures.Add("Gateway:Authentication:Jwt:Audience must be configured.");
        }

        if (string.IsNullOrWhiteSpace(options.InventoryAudience))
        {
            failures.Add("Gateway:Authentication:Jwt:InventoryAudience must be configured.");
        }

        if (!Uri.TryCreate(options.JwksUri, UriKind.Absolute, out Uri? jwksUri)
            || (jwksUri.Scheme != Uri.UriSchemeHttps && jwksUri.Scheme != Uri.UriSchemeHttp))
        {
            failures.Add("Gateway:Authentication:Jwt:JwksUri must be an absolute HTTP or HTTPS URI.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
