using Dprental.PaymentService.Application.Background;
using Dprental.PaymentService.Application.IServices;
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

        services.AddSingleton(TimeProvider.System);

        services.AddScoped<IInvoiceCreationService, InvoiceCreationService>();
        services.AddScoped<IInvoiceLifecycleService, InvoiceLifecycleService>();
        services.AddScoped<IInvoicePaymentProcessor, InvoicePaymentProcessor>();
        services.AddScoped<IInvoiceService, InvoiceService>();

        services.AddHostedService<InvoiceExpiryWorker>();

        return services;
    }
}
