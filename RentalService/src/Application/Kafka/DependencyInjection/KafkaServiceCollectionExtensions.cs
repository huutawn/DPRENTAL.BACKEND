using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Consumers;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Kafka.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Application.Kafka.DependencyInjection;

public static class KafkaServiceCollectionExtensions
{
    public static IServiceCollection AddKafka(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Bind and validate options
        services.AddOptions<KafkaOptions>()
            .Bind(configuration.GetSection(KafkaOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<KafkaOptions>, KafkaOptionsValidator>();

        // Register shared producer transport
        services.AddSingleton<IKafkaProducerTransport>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<KafkaOptions>>();
            var logger = sp.GetRequiredService<ILogger<KafkaProducerTransport>>();
            return new KafkaProducerTransport(options, logger);
        });

        // Register shared outbox writer
        services.AddScoped<IKafkaOutboxWriter, KafkaOutboxWriter>();

        // Register business producers
        services.AddScoped<IInventoryCommandProducer, InventoryCommandProducer>();
        services.AddScoped<IPaymentCommandProducer, PaymentCommandProducer>();
        services.AddScoped<IRentalEventProducer, RentalEventProducer>();

        // Register consumers / dispatchers
        services.AddScoped<IPaymentEventsDispatcher, PaymentEventsDispatcher>();
        services.AddScoped<InventoryReturnResultHandler>();

        // Register hosted workers
        services.AddHostedService<OutboxPublisherWorker>();
        services.AddHostedService<KafkaPaymentEventsConsumerWorker>();
        services.AddHostedService<KafkaInventoryReturnConsumerWorker>();
        services.AddHostedService<InboxProcessingWorker>();

        return services;
    }
}
