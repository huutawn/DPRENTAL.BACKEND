using Dprental.AnalyticsService.Application.Dashboard;
using Dprental.AnalyticsService.Application.Dashboard.Revenue;
using Dprental.AnalyticsService.Application.Kafka.Configuration;
using Dprental.AnalyticsService.Application.Kafka.Consumers;
using Dprental.AnalyticsService.Application.Projections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Dprental.AnalyticsService.Application.DependencyInjection;

public static class AnalyticsApplicationExtensions
{
    /// <summary>Projector (ghi read model) và query service của dashboard.</summary>
    public static IServiceCollection AddAnalyticsApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Lấy giờ qua TimeProvider (thay được trong test), không dùng DateTimeOffset.UtcNow
        services.TryAddSingleton(TimeProvider.System);

        // Scoped: cùng DbContext trong phạm vi xử lý một message / một request
        services.AddScoped<ReadModelChangeSet>();
        services.AddScoped<RentalOrderProjector>();
        services.AddScoped<RentalItemProjector>();
        services.AddScoped<PaymentFinancialProjector>();

        services.AddScoped<DashboardQueryService>();
        services.AddScoped<OrderTrendQueryService>();
        services.AddScoped<RevenueAnalyticsQueryService>();
        return services;
    }

    /// <summary>Cấu hình Kafka (kiểm tra lúc khởi động) và pipeline xử lý message. Worker đăng ký ở Api.</summary>
    public static IServiceCollection AddAnalyticsKafka(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AnalyticsKafkaOptions>()
            .Bind(configuration.GetSection(AnalyticsKafkaOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AnalyticsKafkaOptions>, AnalyticsKafkaOptionsValidator>();

        services.AddSingleton<PaymentProofVerifier>();
        services.AddScoped<IAnalyticsEventDispatcher, AnalyticsEventDispatcher>();
        services.AddSingleton<KafkaRecordProcessor>(); // Tự tạo scope mới cho mỗi lần xử lý message
        return services;
    }
}
