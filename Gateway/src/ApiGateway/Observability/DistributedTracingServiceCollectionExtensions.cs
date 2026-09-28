using System.Diagnostics;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using Dprental.Gateway.Domain.Observability;

namespace Dprental.Gateway.Observability;

/// <summary>
/// Đăng ký W3C trace context và OpenTelemetry cho HTTP ingress và HTTP egress.
/// </summary>
internal static class DistributedTracingServiceCollectionExtensions
{
    private const string ServiceName = "dprental-api-gateway";

    /// <summary>
    /// Thiết lập lấy mẫu trace gốc, tạo span cho HTTP và chỉ xuất OTLP khi môi trường đã cấu hình collector.
    /// </summary>
    internal static IServiceCollection AddGatewayDistributedTracing(this IServiceCollection services, IConfiguration configuration)
    {
        // Bắt buộc traceparent theo chuẩn W3C để Gateway và các service .NET dùng cùng định dạng định danh trace.
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
                    .AddAspNetCoreInstrumentation(instrumentation =>
                    {
                        // Health probe có tần suất cao, không mang giá trị chẩn đoán request nghiệp vụ nên không tạo span.
                        instrumentation.Filter = context =>
                            !context.Request.Path.StartsWithSegments("/_health", StringComparison.Ordinal) &&
                            !context.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal);
                    })
                    .AddHttpClientInstrumentation();

                if (!string.IsNullOrWhiteSpace(options.Otlp.TracesEndpoint))
                {
                    tracing.AddOtlpExporter(exporter =>
                    {
                        exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                        exporter.Endpoint = new Uri(options.Otlp.TracesEndpoint, UriKind.Absolute);
                    });
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(GatewayMetricNames.MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();
            });
        return services;
    }
}
