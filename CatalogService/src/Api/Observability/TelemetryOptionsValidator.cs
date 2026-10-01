using Microsoft.Extensions.Options;

namespace Dprental.CatalogService.Api.Observability;

/// <summary>Xác thực tỷ lệ lấy mẫu và endpoint OTLP ngay khi ứng dụng khởi động.</summary>
internal sealed class TelemetryOptionsValidator : IValidateOptions<TelemetryOptions>
{
    /// <summary>
    /// Chặn cấu hình sai ngay lúc khởi động để request không bị mất trace âm thầm khi ứng dụng đang chạy.
    /// </summary>
    public ValidateOptionsResult Validate(string? name, TelemetryOptions options)
    {
        List<string> failures = [];
        if (options.Tracing.SamplingRatio is < 0 or > 1 || double.IsNaN(options.Tracing.SamplingRatio))
        {
            failures.Add("Telemetry:Tracing:SamplingRatio phải nằm trong khoảng từ 0 đến 1.");
        }

        if (!string.IsNullOrWhiteSpace(options.Otlp.TracesEndpoint)
            && (!Uri.TryCreate(options.Otlp.TracesEndpoint, UriKind.Absolute, out Uri? endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
                || !endpoint.AbsolutePath.EndsWith("/v1/traces", StringComparison.Ordinal)))
        {
            failures.Add("Telemetry:Otlp:TracesEndpoint phải là URL HTTP hoặc HTTPS tuyệt đối và kết thúc bằng /v1/traces.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
