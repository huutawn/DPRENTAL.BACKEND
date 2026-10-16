using Dprental.PaymentService.Application.Background;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Kafka;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Application.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.PaymentService.Application.DependencyInjection;

/// <summary>
/// Đăng ký Application services, background workers và options cho PaymentService.
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddPaymentApplication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<PaymentMerchantOptions>()
            .Bind(configuration.GetSection(PaymentMerchantOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => !string.IsNullOrWhiteSpace(o.Bank) && !string.IsNullOrWhiteSpace(o.Account),
                "Merchant Bank and Account must be non-empty.")
            .ValidateOnStart();

        services.AddOptions<SePayOptions>()
            .Bind(configuration.GetSection(SePayOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "SePay ApiKey must be non-empty.")
            .ValidateOnStart();

        services.AddOptions<PaymentKafkaOptions>()
            .Bind(configuration.GetSection(PaymentKafkaOptions.SectionName))
            .Validate(o => !o.Enabled ||
                (!string.IsNullOrWhiteSpace(o.BootstrapServers) &&
                 !string.IsNullOrWhiteSpace(o.PaymentEventsTopic) &&
                 o.BatchSize > 0 && o.PollIntervalSeconds > 0 && o.DeliveryTimeoutSeconds > 0),
                "Enabled Kafka requires broker, topic and positive worker settings.")
            .ValidateOnStart();

        if (configuration.GetValue<bool>("Kafka:Enabled"))
            services.AddSingleton<IEventTransport, KafkaEventTransport>();

        services.AddSingleton(TimeProvider.System);

        services.AddScoped<IInvoiceCreationService, InvoiceCreationService>();
        services.AddScoped<IInvoiceLifecycleService, InvoiceLifecycleService>();
        services.AddScoped<IInvoicePaymentProcessor, InvoicePaymentProcessor>();
        services.AddScoped<IInvoiceService, InvoiceService>();
        services.AddScoped<ISettlementService, SettlementService>();
        services.AddScoped<IManualRefundService, ManualRefundService>();

        services.AddHostedService<InvoiceExpiryWorker>();
        services.AddHostedService<PaymentOutboxPublisherWorker>();

        return services;
    }
}
