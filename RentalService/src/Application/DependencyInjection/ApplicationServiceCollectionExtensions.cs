using System.Net.Http;
using Dprental.RentalService.Application.Background;
using Dprental.RentalService.Application.Kafka.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.IServices.Calendar;
using Dprental.RentalService.Application.IServices.Checkout;
using Dprental.RentalService.Application.IServices.Expiry;
using Dprental.RentalService.Application.IServices.Integration;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.IServices.Payments;
using Dprental.RentalService.Application.IServices.SupplierDecision;
using Dprental.RentalService.Application.IServices.SupplierStatistics;
using Dprental.RentalService.Application.Observability;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Calendar;
using Dprental.RentalService.Application.Services.Cart;
using Dprental.RentalService.Application.Services.Checkout;
using Dprental.RentalService.Application.Services.Expiry;
using Dprental.RentalService.Application.Services.Integration;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.Payments;
using Dprental.RentalService.Application.Services.SupplierDecision;
using Dprental.RentalService.Application.Services.SupplierStatistics;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.RentalService.Application.DependencyInjection;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddRentalServiceApplication(this IServiceCollection services, IConfiguration? configuration = null)
    {
        // Cart Use Cases & Read Models
        services.AddScoped<RentalCartUseCases>();
        services.AddScoped<GetRentalCart>();

        // Cart Policy & Adapter
        services.AddSingleton<IRentalCartPolicy, RentalCartOptions>();
        services.AddScoped<InventoryRentalCartReader>();
        services.AddScoped<IRentalListingOwnerProvider>(sp => sp.GetRequiredService<InventoryRentalCartReader>());
        services.AddScoped<IRentalListingReader>(sp => sp.GetRequiredService<InventoryRentalCartReader>());
        services.AddScoped<IRentalAvailabilityReader>(sp => sp.GetRequiredService<InventoryRentalCartReader>());

        // External Clients
        services.AddHttpClient<IServiceTokenProvider, AuthServiceTokenProvider>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpClient<IPaymentRentalClient, HttpPaymentRentalClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpClient<IInventoryRentalClient, HttpInventoryRentalClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<IInventoryListingClient, UnavailableInventoryListingClient>();
        services.AddSingleton<ICatalogReferenceClient, UnavailableCatalogReferenceClient>();

        // Checkout
        services.AddScoped<ICheckoutPreparationService, CheckoutPreparationService>();
        services.AddScoped<ICheckoutHoldOrchestrator, CheckoutHoldOrchestrator>();
        services.AddScoped<ICheckoutOrderWriter, CheckoutOrderWriter>();
        services.AddScoped<ICheckoutInvoiceIssuer, CheckoutInvoiceIssuer>();
        services.AddScoped<ICheckoutService, CheckoutService>();

        // Calendar
        services.AddScoped<IRentalCalendarService, RentalCalendarService>();

        // Operations
        services.AddScoped<IRentalOperationExecutor, RentalOperationExecutor>();

        // Payments
        services.AddScoped<IPaymentWorkflowService, PaymentWorkflowService>();

        // Supplier Decision & Statistics
        services.AddScoped<ISupplierDecisionService, SupplierDecisionService>();
        services.AddScoped<ISupplierStatisticsService, SupplierStatisticsService>();

        // Expiry
        services.AddScoped<IExpiryWorkflowService, ExpiryWorkflowService>();

        // Integration
        services.AddScoped<IRefundCompletedConsumer, RefundCompletedConsumer>();

        // Kafka subsystem (producers, consumers, workers, options)
        services.AddKafka(configuration ?? new ConfigurationBuilder().Build());

        // Non-Kafka Background Workers
        services.AddHostedService<OperationRecoveryWorker>();
        services.AddHostedService<ExpiryScannerWorker>();

        // Observability & TimeProvider
        services.AddSingleton<RentalMetrics>();
        services.AddSingleton(TimeProvider.System);

        return services;
    }
}
