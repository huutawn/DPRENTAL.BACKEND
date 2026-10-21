using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Kafka.Options;
using Dprental.InventoryService.Application.Kafka.Outbox;
using Dprental.InventoryService.Application.Kafka.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.InventoryService.Application.Kafka.DependencyInjection;

/// <summary>
/// Mở rộng IServiceCollection đăng ký toàn bộ Kafka infrastructure, Outbox publisher và Consumer workers.
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
        registry.Register<SearchProjectionMessageHandler, SearchProjectionChangedPayload>(
            topic: KafkaTopics.InventoryProjectionEventsV1,
            messageType: SearchProjectionEvents.ListingMessageType,
            version: 1,
            handlerName: nameof(SearchProjectionMessageHandler) + "_Listing",
            groupId: SearchProjectionMessageHandler.ConsumerGroupId);

        registry.Register<SearchProjectionMessageHandler, SearchProjectionChangedPayload>(
            topic: KafkaTopics.InventoryProjectionEventsV1,
            messageType: SearchProjectionEvents.AssetMessageType,
            version: 1,
            handlerName: nameof(SearchProjectionMessageHandler) + "_Asset",
            groupId: SearchProjectionMessageHandler.ConsumerGroupId);

        // Đăng ký Allocation Commands handlers
        registry.Register<AllocationCommandHandler, CreateHoldPayload>(
            topic: KafkaTopics.InventoryAllocationCommandsV1,
            messageType: AllocationMessageTypes.CreateHold,
            version: 1,
            handlerName: $"{nameof(AllocationCommandHandler)}_{AllocationMessageTypes.CreateHold}",
            groupId: AllocationCommandHandler.ConsumerGroupId);

        registry.Register<AllocationCommandHandler, ExtendHoldPayload>(
            topic: KafkaTopics.InventoryAllocationCommandsV1,
            messageType: AllocationMessageTypes.ExtendHold,
            version: 1,
            handlerName: $"{nameof(AllocationCommandHandler)}_{AllocationMessageTypes.ExtendHold}",
            groupId: AllocationCommandHandler.ConsumerGroupId);

        registry.Register<AllocationCommandHandler, ConfirmAllocationPayload>(
            topic: KafkaTopics.InventoryAllocationCommandsV1,
            messageType: AllocationMessageTypes.ConfirmAllocation,
            version: 1,
            handlerName: $"{nameof(AllocationCommandHandler)}_{AllocationMessageTypes.ConfirmAllocation}",
            groupId: AllocationCommandHandler.ConsumerGroupId);

        registry.Register<AllocationCommandHandler, ReleaseAllocationPayload>(
            topic: KafkaTopics.InventoryAllocationCommandsV1,
            messageType: AllocationMessageTypes.ReleaseAllocation,
            version: 1,
            handlerName: $"{nameof(AllocationCommandHandler)}_{AllocationMessageTypes.ReleaseAllocation}",
            groupId: AllocationCommandHandler.ConsumerGroupId);

        registry.Register<AllocationCommandHandler, MarkAssetsHandedOverPayload>(
            topic: KafkaTopics.InventoryAllocationCommandsV1,
            messageType: AllocationMessageTypes.MarkAssetsHandedOver,
            version: 1,
            handlerName: $"{nameof(AllocationCommandHandler)}_{AllocationMessageTypes.MarkAssetsHandedOver}",
            groupId: AllocationCommandHandler.ConsumerGroupId);

        registry.Register<AllocationCommandHandler, MarkAssetsOverduePayload>(
            topic: KafkaTopics.InventoryAllocationCommandsV1,
            messageType: AllocationMessageTypes.MarkAssetsOverdue,
            version: 1,
            handlerName: $"{nameof(AllocationCommandHandler)}_{AllocationMessageTypes.MarkAssetsOverdue}",
            groupId: AllocationCommandHandler.ConsumerGroupId);

        registry.Register<AllocationCommandHandler, ApplyReturnedUnitsCommandPayload>(
            topic: KafkaTopics.InventoryAllocationCommandsV1,
            messageType: AllocationMessageTypes.ApplyReturnedUnits,
            version: 1,
            handlerName: $"{nameof(AllocationCommandHandler)}_{AllocationMessageTypes.ApplyReturnedUnits}",
            groupId: AllocationCommandHandler.ConsumerGroupId);
        configureHandlers?.Invoke(registry);
        services.AddSingleton(registry);
        services.AddScoped<SearchProjectionMessageHandler>();
        services.AddScoped<AllocationCommandHandler>();

        // Đăng ký Kafka Transport
        services.AddSingleton<KafkaProducer>();
        services.AddSingleton<IKafkaProducer>(sp => sp.GetRequiredService<KafkaProducer>());
        services.AddSingleton<KafkaTopicProvisioner>();

        // Đăng ký background workers
        services.AddHostedService<KafkaTopicStartupTask>();
        services.AddHostedService<OutboxPublisherWorker>();
        services.AddHostedService<KafkaConsumerWorker>();

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
