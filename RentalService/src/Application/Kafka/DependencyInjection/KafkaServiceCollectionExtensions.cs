using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Consumers;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Kafka.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Application.Kafka.DependencyInjection;

/// <remarks>
/// Program → AddRentalServiceApplication → AddKafka: bind/validate cấu hình, đăng ký transport singleton,
/// writer/producer/dispatcher scoped và ba hosted worker gửi Outbox, nhận Kafka, xử lý Inbox.
/// Đăng ký DI không tự bật Kafka; các worker kiểm tra Enabled trước khi chạy.
/// </remarks>
public static class KafkaServiceCollectionExtensions
{
    public static IServiceCollection AddKafka(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Đọc section Kafka và kiểm tra cấu hình lúc startup trước khi worker chạy.
        services.AddOptions<KafkaOptions>()
            .Bind(configuration.GetSection(KafkaOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<KafkaOptions>, KafkaOptionsValidator>();

        // Transport singleton tái sử dụng producer kết nối broker; không giữ DbContext scoped.
        services.AddSingleton<IKafkaProducerTransport>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<KafkaOptions>>();
            var logger = sp.GetRequiredService<ILogger<KafkaProducerTransport>>();
            return new KafkaProducerTransport(options, logger);
        });

        // Writer scoped dùng repository trong cùng scope/transaction của use case.
        services.AddScoped<IKafkaOutboxWriter, KafkaOutboxWriter>();

        // Producer nghiệp vụ dựng contract và ghi Outbox, chưa gửi broker.
        services.AddScoped<IInventoryCommandProducer, InventoryCommandProducer>();
        services.AddScoped<IRentalEventProducer, RentalEventProducer>();

        // Dispatcher tiếp nhận và phân phối Inbox tới handler Payment/Inventory.
        services.AddScoped<IPaymentEventsDispatcher, PaymentEventsDispatcher>();
        services.AddScoped<IInventoryAllocationResultHandler, InventoryAllocationResultHandler>();
        services.AddScoped<InventoryReturnResultHandler>();

        // Ba vòng chạy độc lập: gửi Outbox, nhận broker, xử lý Inbox đã lưu.
        services.AddHostedService<OutboxPublisherWorker>();
        services.AddHostedService<KafkaPaymentEventsConsumerWorker>();
        services.AddHostedService<InboxProcessingWorker>();

        return services;
    }
}
