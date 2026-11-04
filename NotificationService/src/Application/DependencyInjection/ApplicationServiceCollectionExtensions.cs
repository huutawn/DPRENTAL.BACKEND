using Dprental.NotificationService.Application.IServices;
using Dprental.NotificationService.Application.Kafka.Consumers;
using Dprental.NotificationService.Application.Kafka.Handlers;
using Dprental.NotificationService.Application.Kafka.Quarantine;
using Dprental.NotificationService.Application.Kafka.Services;
using Dprental.NotificationService.Application.Recipients;
using Dprental.NotificationService.Application.Services;
using Dprental.NotificationService.Application.Templates;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.NotificationService.Application.DependencyInjection;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationServiceApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<IRecipientResolutionService, RecipientResolutionService>();
        services.AddSingleton<INotificationTemplateRenderer, NotificationTemplateRenderer>();

        // Kafka Event Handlers (all 10 events)
        services.AddSingleton<IKafkaEventHandler, RentalOrderCreatedHandler>();
        services.AddSingleton<IKafkaEventHandler, RentalOrderPaidHandler>();
        services.AddSingleton<IKafkaEventHandler, RentalSupplierConfirmationRequestedHandler>();
        services.AddSingleton<IKafkaEventHandler, RentalOrderExpiredHandler>();
        services.AddSingleton<IKafkaEventHandler, RentalOrderCancelledHandler>();
        services.AddSingleton<IKafkaEventHandler, RentalItemHandoverCompletedHandler>();
        services.AddSingleton<IKafkaEventHandler, RentalHandoverOverdueHandler>();
        services.AddSingleton<IKafkaEventHandler, ReturnConfirmationOverdueHandler>();
        services.AddSingleton<IKafkaEventHandler, RentalOrderCompletedHandler>();
        services.AddSingleton<IKafkaEventHandler, RentalDeductionDisputeResolvedHandler>();
        services.AddSingleton<IKafkaEventHandler, LatePaymentRefundRequestedHandler>();

        // Kafka Ingestion & DLQ
        services.AddSingleton<IKafkaDeadLetterPublisher, KafkaDeadLetterPublisher>();
        services.AddScoped<INotificationIngestionService, NotificationIngestionService>();
        services.AddHostedService<KafkaConsumerHostedService>();

        // Application Inbox Services
        services.AddScoped<INotificationQueryService, NotificationQueryService>();
        services.AddScoped<INotificationReadService, NotificationReadService>();
        services.AddScoped<IPreferenceService, PreferenceService>();

        return services;
    }
}
