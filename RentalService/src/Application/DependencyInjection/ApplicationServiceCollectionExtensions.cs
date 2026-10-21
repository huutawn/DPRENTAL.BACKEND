using Dprental.RentalService.Application.Background;
using Dprental.RentalService.Application.Kafka.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.IServices.Addresses;
using Dprental.RentalService.Application.IServices.Calendar;
using Dprental.RentalService.Application.IServices.Checkout;
using Dprental.RentalService.Application.IServices.Expiry;
using Dprental.RentalService.Application.IServices.Integration;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.IServices.Orders;
using Dprental.RentalService.Application.IServices.Overdue;
using Dprental.RentalService.Application.IServices.Payments;
using Dprental.RentalService.Application.IServices.Returns;
using Dprental.RentalService.Application.IServices.SupplierDecision;
using Dprental.RentalService.Application.IServices.SupplierStatistics;
using Dprental.RentalService.Application.Observability;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Addresses;
using Dprental.RentalService.Application.Services.Calendar;
using Dprental.RentalService.Application.Services.Cart;
using Dprental.RentalService.Application.Services.Checkout;
using Dprental.RentalService.Application.Services.Expiry;
using Dprental.RentalService.Application.Services.Integration;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.Orders;
using Dprental.RentalService.Application.Services.Payments;
using Dprental.RentalService.Application.Services.Returns;
using Dprental.RentalService.Application.Services.SupplierDecision;
using Dprental.RentalService.Application.Services.SupplierStatistics;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http;
using Dprental.RentalService.Application.Services.Overdue;
using Dprental.RentalService.Application.IServices.Cancellation;
using Dprental.RentalService.Application.Services.Cancellation;
using Dprental.RentalService.Application.IServices.Handover;
using Dprental.RentalService.Application.Services.Handover;
using Dprental.RentalService.Application.IServices.Settlement;
using Dprental.RentalService.Application.Services.Settlement;

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
        services.AddScoped<IRentalListingReader>(sp => sp.GetRequiredService<InventoryRentalCartReader>());
        services.AddScoped<IRentalAvailabilityReader>(sp => sp.GetRequiredService<InventoryRentalCartReader>());

        // External Clients
        services.AddHttpClient<IRentalMediaClient, HttpRentalMediaClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpClient<IPaymentRentalClient, HttpPaymentRentalClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpClient<IInventoryRentalClient, HttpInventoryRentalClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        // Checkout
        services.AddScoped<ICheckoutPreparationService, CheckoutPreparationService>();
        services.AddScoped<ICheckoutOrderWriter, CheckoutOrderWriter>();
        services.AddScoped<ICheckoutInvoiceIssuer, CheckoutInvoiceIssuer>();
        services.AddScoped<ICheckoutService, CheckoutService>();

        // Orders
        services.AddScoped<IRenterOrderQueryService, RenterOrderQueryService>();
        services.AddScoped<IRenterOrderCancellationService, RenterOrderCancellationService>();
        services.AddScoped<ISupplierSubOrderQueryService, SupplierSubOrderQueryService>();
        services.AddScoped<IAdminOrderQueryService, AdminOrderQueryService>();
        services.AddScoped<IRenterDeliveryNoteService, RenterDeliveryNoteService>();

        // Returns
        services.AddScoped<IReturnService, ReturnService>();

        // Calendar
        services.AddScoped<IRentalCalendarService, RentalCalendarService>();

        // Addresses
        services.AddScoped<IRenterAddressService, RenterAddressService>();

        // Operations
        services.AddScoped<IRentalOperationExecutor, RentalOperationExecutor>();

        // Payments
        services.AddScoped<IPaymentWorkflowService, PaymentWorkflowService>();

        // Supplier Decision & Statistics
        services.AddScoped<ISupplierDecisionService, SupplierDecisionService>();
        services.AddScoped<ISupplierStatisticsService, SupplierStatisticsService>();

        // Handover Acknowledgement & Item Cancellation
        services.AddScoped<IHandoverAcknowledgementService, HandoverAcknowledgementService>();
        services.AddScoped<IItemCancellationService, ItemCancellationService>();

        // Expiry
        services.AddScoped<IExpiryWorkflowService, ExpiryWorkflowService>();

        // Integration
        services.AddScoped<IRefundCompletedConsumer, RefundCompletedConsumer>();
        services.AddScoped<IRentalSettledConsumer, RentalSettledConsumer>();

        // Kafka subsystem (producers, consumers, workers, options)
        services.AddKafka(configuration ?? new ConfigurationBuilder().Build());

        // Non-Kafka Background Workers
        services.AddHostedService<OperationRecoveryWorker>();
        services.AddHostedService<ExpiryScannerWorker>();
        services.AddHostedService<OverdueProtectionWorker>();
        services.AddHostedService<CheckoutTimeoutScannerWorker>();

        // Observability & TimeProvider
        services.AddSingleton<RentalMetrics>();
        services.AddSingleton(TimeProvider.System);

        // Overdue protection
        services.AddOptions<OverdueProtectionOptions>()
            .BindConfiguration(OverdueProtectionOptions.SectionName);
        services.AddScoped<IOverdueProtectionService, OverdueProtectionService>();

        // Handover Overdue & Auto-Cancellation
        services.AddOptions<HandoverOverdueOptions>()
            .BindConfiguration(HandoverOverdueOptions.SectionName)
            .Validate(o => o.AutoExpireGraceHours is >= 1 and <= 168,
                "Rental:HandoverOverdue:AutoExpireGraceHours must be between 1 and 168.")
            .ValidateOnStart();
        services.AddScoped<IHandoverOverdueService, HandoverOverdueService>();
        services.AddHostedService<HandoverOverdueWorker>();

        // Settlement
        services.AddScoped<IDeductionService, DeductionService>();
        services.AddScoped<IDeductionResponseService, DeductionResponseService>();
        // trong khối Background Workers:
        services.AddHostedService<DeductionDeadlineWorker>();


        return services;
    }
}
