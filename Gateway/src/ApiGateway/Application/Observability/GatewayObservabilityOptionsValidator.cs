using Microsoft.Extensions.Options;

namespace Dprental.Gateway.Application.Observability;

/// <summary>
/// Thẩm định tính hợp lệ của cấu hình Observability (Logging & Metrics) khi Gateway khởi động.
/// </summary>
public sealed class GatewayObservabilityOptionsValidator : IValidateOptions<GatewayObservabilityOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, GatewayObservabilityOptions? options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("Cấu hình Gateway:Observability không được để trống.");
        }

        List<string> failures = new();

        if (options.Logging.RedactedHeaderNames is null || options.Logging.RedactedHeaderNames.Length == 0)
        {
            failures.Add("Gateway:Observability:Logging:RedactedHeaderNames phải chứa ít nhất tiêu đề 'Authorization'.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
