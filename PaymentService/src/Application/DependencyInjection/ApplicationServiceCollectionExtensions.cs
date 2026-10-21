using Dprental.PaymentService.Application.Background;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Kafka;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Application.Services;
using Dprental.PaymentService.Common.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
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
            .ValidateDataAnnotations()
            .Validate(o => !o.Enabled ||
                (!string.IsNullOrWhiteSpace(o.BootstrapServers) &&
                 !string.IsNullOrWhiteSpace(o.Topic) &&
                 o.BatchSize > 0 && o.PollingIntervalSeconds > 0 &&
                 o.DeliveryTimeoutSeconds > 0 && o.LeaseDurationSeconds > 0),
                "Enabled Kafka requires broker, topic and positive worker settings.")
            .ValidateOnStart();
        services.AddOptions<IntegrityVerificationOptions>()
            .BindConfiguration(IntegrityVerificationOptions.SectionName);
        services.AddOptions<PaymentEncryptionOptions>()
            .BindConfiguration(PaymentEncryptionOptions.SectionName);
        services.AddSingleton<IPaymentDataEncryptor>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<PaymentEncryptionOptions>>().Value;
            return new Dprental.PaymentService.Common.Cryptography.PaymentDataEncryptor(opt.GetKeyBytes(), opt.ActiveKeyId, opt.GetOldKeyBytes());
        });
        services.AddSingleton(TimeProvider.System);

        services.AddScoped<IInvoiceCreationService, InvoiceCreationService>();
        services.AddScoped<IInvoiceLifecycleService, InvoiceLifecycleService>();
        services.AddScoped<IInvoicePaymentProcessor, InvoicePaymentProcessor>();
        services.AddScoped<ISettlementService, SettlementService>();
        services.AddScoped<IManualRefundService, ManualRefundService>();
        services.AddScoped<IFinancialReadService, FinancialReadService>();
        services.AddScoped<ISupplierPayoutService, SupplierPayoutService>();
        services.AddScoped<ISupplierPayoutDestinationService, SupplierPayoutDestinationService>();
        services.AddScoped<ITransactionResolutionService, TransactionResolutionService>();
        services.AddSingleton<IInvoicePaidKafkaProducer, InvoicePaidKafkaProducer>();

        services.AddHostedService<InvoiceExpiryWorker>();
        services.AddHostedService<InvoicePaidOutboxPublisherWorker>();

        return services;
    }
}
