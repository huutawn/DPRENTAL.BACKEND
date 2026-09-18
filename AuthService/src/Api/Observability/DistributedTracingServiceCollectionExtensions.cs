using System.Diagnostics;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Dprental.AuthService.Api.Observability;

/// <summary>
/// Đăng ký W3C trace context và OpenTelemetry cho các HTTP request vào và ra của AuthService.
/// </summary>
internal static class DistributedTracingServiceCollectionExtensions
{
    private const string ServiceName = "dprental-auth-service";

    /// <summary>
    /// Thiết lập lấy mẫu trace gốc, tạo span HTTP và chỉ xuất OTLP khi môi trường đã cấu hình collector.
    /// </summary>
    internal static IServiceCollection AddAuthServiceDistributedTracing(this IServiceCollection services, IConfiguration configuration)
    {
        // Bắt buộc traceparent theo chuẩn W3C để AuthService tiếp tục đúng trace do Gateway gửi đến.
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;

        TelemetryOptions options = configuration.GetRequiredSection(TelemetryOptions.SectionName).Get<TelemetryOptions>() ?? new TelemetryOptions();
        TelemetryOptionsValidator validator = new();
        ValidateOptionsResult validation = validator.Validate(null, options);
        if (validation.Failed)
        {
            throw new OptionsValidationException(TelemetryOptions.SectionName, typeof(TelemetryOptions), validation.Failures);
        }

        services.AddSingleton<IValidateOptions<TelemetryOptions>, TelemetryOptionsValidator>();
        services.AddOptions<TelemetryOptions>()
            .Bind(configuration.GetRequiredSection(TelemetryOptions.SectionName))
            .ValidateOnStart();

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(options.Tracing.SamplingRatio)))
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (!string.IsNullOrWhiteSpace(options.Otlp.TracesEndpoint))
                {
                    tracing.AddOtlpExporter(exporter =>
                    {
                        exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                        exporter.Endpoint = new Uri(options.Otlp.TracesEndpoint, UriKind.Absolute);
                    });
                }
            });

        return services;
    }
}
