using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Kafka.Options;
using Dprental.InventoryService.Application.Kafka.Outbox;
using Dprental.InventoryService.Application.Kafka.Saga;
using Dprental.InventoryService.Application.Kafka.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.InventoryService.Application.Kafka.DependencyInjection;

/// <summary>
/// Mở rộng IServiceCollection đăng ký toàn bộ Kafka infrastructure, Outbox publisher, Consumer workers và Saga runtime.
/// </summary>
public static class KafkaServiceCollectionExtensions
{
    public static IServiceCollection AddInventoryKafkaInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<KafkaHandlerRegistry>? configureHandlers = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Đăng ký Kafka options
        var kafkaSection = configuration.GetSection(KafkaOptions.SectionName);
        services.Configure<KafkaOptions>(kafkaSection);

        // Đăng ký Handler Registry
        var registry = new KafkaHandlerRegistry();
        configureHandlers?.Invoke(registry);
        services.AddSingleton(registry);

        // Đăng ký Kafka Transport
        services.AddSingleton<KafkaProducer>();
        services.AddSingleton<IKafkaProducer>(sp => sp.GetRequiredService<KafkaProducer>());
        services.AddSingleton<KafkaTopicProvisioner>();

        // Đăng ký Saga runtime
        services.AddScoped<ISagaOrchestrator, SagaOrchestrator>();

        // Đăng ký background workers
        services.AddHostedService<KafkaTopicStartupTask>();
        services.AddHostedService<OutboxPublisherWorker>();
        services.AddHostedService<KafkaConsumerWorker>();
        services.AddHostedService<SagaTimeoutWorker>();

        return services;
    }
}

/// <summary>
/// Hosted service chạy kiểm tra hoặc provision topics khi ứng dụng khởi động.
/// Đảm bảo broker outage không ngăn cản API khởi động hoặc nhận transaction PostgreSQL.
/// </summary>
public sealed class KafkaTopicStartupTask : IHostedService
{
    private readonly KafkaTopicProvisioner _provisioner;
    private readonly ILogger<KafkaTopicStartupTask>? _logger;

    public KafkaTopicStartupTask(KafkaTopicProvisioner provisioner, ILogger<KafkaTopicStartupTask>? logger = null)
    {
        _provisioner = provisioner;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _provisioner.EnsureTopicsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Không thể kết nối đến Kafka broker trong quá trình startup. API vẫn tiếp tục hoạt động với PostgreSQL bình thường.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
