using Dprental.RecommendationService.Application.IServices;
using Dprental.RecommendationService.Application.Services;
using Dprental.RecommendationService.Application.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.RecommendationService.Application.DependencyInjection;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddRecommendationServiceApplication(
        this IServiceCollection services,
        bool enableKafka = true,
        bool enableValkey = true)
    {
        services.AddSingleton<IEventSchemaValidator, EventSchemaValidator>();
        services.AddScoped<IInteractionIngestionService, InteractionIngestionService>();
        services.AddScoped<ISnapshotPublishingService, SnapshotPublishingService>();

        if (enableKafka)
        {
            services.AddSingleton<Kafka.IKafkaClientFactory, Kafka.KafkaClientFactory>();
            services.AddSingleton<Kafka.Producer.IKafkaDlqProducer, Kafka.Producer.KafkaDlqProducer>();
            services.AddHostedService<Kafka.Consumers.RecommendationKafkaConsumerWorker>();
        }

        if (enableValkey)
        {
            services.AddHostedService<Background.ValkeySnapshotWorker>();
        }

        return services;
    }
}
