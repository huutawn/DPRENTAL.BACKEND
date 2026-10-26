using Dprental.AnalyticsService.Application.Services.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dprental.AnalyticsService.Application.DependencyInjection;

/// <summary>
/// Đăng ký các service nghiệp vụ của tầng Application.
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddAnalyticsApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Lấy giờ qua TimeProvider (thay được trong test), không dùng DateTime.UtcNow
        services.TryAddSingleton(TimeProvider.System);

        // Scoped: dùng repository scoped (cùng DbContext) trong phạm vi xử lý một message
        services.AddScoped<ReadModelChangeSet>();
        services.AddScoped<RentalOrderProjector>();
        services.AddScoped<RentalItemProjector>();
        services.AddScoped<FinancialProjector>();

        return services;
    }
}
