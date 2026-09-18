namespace Dprental.AuthService.Api.Observability;

/// <summary>Cấu hình trace phân tán do AuthService phát ra.</summary>
internal sealed class TelemetryOptions
{
    internal const string SectionName = "Telemetry";

    public TracingOptions Tracing { get; init; } = new();

    public OtlpOptions Otlp { get; init; } = new();
}

/// <summary>Kiểm soát tỷ lệ lấy mẫu các trace gốc được tạo tại AuthService.</summary>
internal sealed class TracingOptions
{
    public double SamplingRatio { get; init; } = 0.10;
}

/// <summary>Cấu hình endpoint collector nhận trace theo giao thức OTLP.</summary>
internal sealed class OtlpOptions
{
    /// <summary>
    /// Endpoint HTTP/protobuf không bắt buộc và phải kết thúc bằng <c>/v1/traces</c>.
    /// Để trống khi môi trường chưa có OpenTelemetry Collector.
    /// </summary>
    public string TracesEndpoint { get; init; } = string.Empty;
}
