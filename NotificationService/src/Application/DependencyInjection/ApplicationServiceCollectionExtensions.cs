using Dprental.NotificationService.Application.Email.Services;
using Dprental.NotificationService.Application.IServices;
using Dprental.NotificationService.Application.Kafka.Consumers;
using Dprental.NotificationService.Application.Kafka.Contracts;
using Dprental.NotificationService.Application.Kafka.Handlers;
using Dprental.NotificationService.Application.Kafka.Quarantine;
using Dprental.NotificationService.Application.Kafka.Services;
using Dprental.NotificationService.Application.Recipients;
using Dprental.NotificationService.Application.Services;
using Dprental.NotificationService.Application.Reconciliation;
using Dprental.NotificationService.Application.Templates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dprental.NotificationService.Application.DependencyInjection;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationServiceApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<IRecipientResolutionService, RecipientResolutionService>();
        services.AddSingleton<INotificationTemplateRenderer, NotificationTemplateRenderer>();

        // Kafka Event Handlers (all 14 events)
        services.AddSingleton<IKafkaEventHandler, RentalOrderCreatedHandler>();
        services.AddSingleton<IKafkaEventHandler, RentalOrderPaidHandler>();
        services.AddSingleton<IKafkaEventHandler, RentalSupplierConfirmationRequestedHandler>();
        services.AddSingleton<IKafkaEventHandler, RentalOrderExpiredHandler>();
        services.AddSingleton<IKafkaEventHandler, RentalOrderCancelledHandler>();
        services.AddSingleton<IKafkaEventHandler, RentalItemHandoverCompletedHandler>();
        services.AddSingleton<IKafkaEventHandler, RentalHandoverOverdueHandler>();
        services.AddSingleton<IKafkaEventHandler, ReturnConfirmationOverdueHandler>();
        services.AddSingleton<IKafkaEventHandler, RentalOrderCompletedHandler>();
        services.AddSingleton<IKafkaEventHandler, LatePaymentRefundRequestedHandler>();
        services.AddSingleton<IKafkaEventHandler, RentalItemConfirmedHandler>();
        services.AddSingleton<IKafkaEventHandler, RentalItemRejectedHandler>();
        services.AddSingleton<IKafkaEventHandler, RefundCompletedFinancialHandler>();
        services.AddSingleton<IKafkaEventHandler, SupplierPayoutCompletedHandler>();
        services.AddSingleton<IKafkaEventHandler, RentalOrderRejectedHandler>();
        services.AddSingleton<IKafkaEventHandler, RentalItemExpiredHandler>();
        services.AddSingleton<IKafkaEventHandler, RentalItemCancelledHandler>();
        services.AddSingleton<IKafkaEventHandler, RentalItemReturnedHandler>();
        services.AddSingleton<IKafkaEventHandler, RentalItemCompletedHandler>();
        // Kafka Ingestion, Registry & DLQ
        services.AddSingleton<IKafkaDeadLetterPublisher, KafkaDeadLetterPublisher>();
        services.AddSingleton<IKafkaContractRegistry, KafkaContractRegistry>();
        services.AddScoped<INotificationIngestionService, NotificationIngestionService>();
        services.AddHostedService<KafkaConsumerHostedService>();

        // Application Inbox Services
        services.AddScoped<INotificationQueryService, NotificationQueryService>();
        services.AddScoped<INotificationReadService, NotificationReadService>();
        services.AddScoped<IPreferenceService, PreferenceService>();
        services.AddScoped<IEmailDeliveryService, EmailDeliveryService>();
        services.AddScoped<IJobReconciliationService, JobReconciliationService>();

        return services;
    }
}
