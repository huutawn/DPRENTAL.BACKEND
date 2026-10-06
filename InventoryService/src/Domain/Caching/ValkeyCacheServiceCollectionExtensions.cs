using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Dprental.InventoryService.Domain.Caching;

/// <summary>
/// Mở rộng IServiceCollection đăng ký cấu hình và adapter cho Valkey Read Cache theo DPR-150.
/// </summary>
public static class ValkeyCacheServiceCollectionExtensions
{
    public const string RedisConnectionStringName = "Redis";

    public static IServiceCollection AddInventoryValkeyCache(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(InventoryCacheOptions.SectionName);
        services.Configure<InventoryCacheOptions>(section);

        var options = section.Get<InventoryCacheOptions>() ?? new InventoryCacheOptions();

        if (!options.Enabled)
        {
            services.AddSingleton<IInventoryAvailabilityCache>(NoOpInventoryAvailabilityCache.Instance);
            return services;
        }

        // Validate cấu hình khi enabled
        var connectionString = configuration.GetConnectionString(RedisConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{RedisConnectionStringName} là bắt buộc khi cache ({InventoryCacheOptions.SectionName}:Enabled) được kích hoạt.");
        }

        if (options.TtlSeconds <= 0)
        {
            throw new InvalidOperationException($"{InventoryCacheOptions.SectionName}:TtlSeconds phải lớn hơn 0.");
        }

        if (options.TimeoutMs <= 0)
        {
            throw new InvalidOperationException($"{InventoryCacheOptions.SectionName}:TimeoutMs phải lớn hơn 0.");
        }

        // ConnectionMultiplexer dùng chung, kết nối nền/lười, không chặn startup khi mất kết nối
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var config = ConfigurationOptions.Parse(connectionString);
            config.AbortOnConnectFail = false;
            config.ConnectTimeout = options.TimeoutMs;
            config.SyncTimeout = options.TimeoutMs;
            config.AsyncTimeout = options.TimeoutMs;
            config.ConnectRetry = 3;
            config.KeepAlive = 60;
            return ConnectionMultiplexer.Connect(config);
        });

        services.AddSingleton<IInventoryAvailabilityCache>(sp =>
            new ValkeyInventoryAvailabilityCache(
                sp.GetRequiredService<IOptions<InventoryCacheOptions>>(),
                sp.GetService<IConnectionMultiplexer>(),
                sp.GetService<TimeProvider>(),
                sp.GetService<ILogger<ValkeyInventoryAvailabilityCache>>()));

        return services;
    }
}
