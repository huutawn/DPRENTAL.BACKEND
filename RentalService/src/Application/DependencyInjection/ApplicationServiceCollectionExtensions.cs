using Dprental.RentalService.Application.Background;
using Dprental.RentalService.Application.Kafka.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.IServices.Addresses;
using Dprental.RentalService.Application.IServices.BankAccounts;
using Dprental.RentalService.Application.IServices.Refunds;
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
using Dprental.RentalService.Application.Services.BankAccounts;
using Dprental.RentalService.Application.Services.Refunds;
using Dprental.RentalService.Application.Services.Calendar;
using Dprental.RentalService.Application.Services.Cart;
using Dprental.RentalService.Application.Services.Checkout;
using Dprental.RentalService.Application.Services.Orders;
using Dprental.RentalService.Application.Services.Expiry;
using Dprental.RentalService.Application.Services.Integration;
using Dprental.RentalService.Application.Services.Operations;
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

/// <remarks>
/// Bản đồ ghép Application: đăng ký use case scoped, client HTTP, executor, Kafka và worker theo lịch.
/// Service scoped dùng repository/unit of work trong scope; hosted worker phải tạo scope khi xử lý.
/// Program bổ sung context người gọi, JWT và notifier của API; không lấy service scoped làm trạng thái dùng chung của worker.
/// </remarks>
public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddRentalServiceApplication(this IServiceCollection services, IConfiguration? configuration = null)
    {
        int windowHours = configuration?.GetValue<int?>("Rental:Deduction:WindowHours") ?? 48;
        int extensionHours = configuration?.GetValue<int?>("Rental:Deduction:DisputeExtensionHours") ?? 12;
        int holdDays = configuration?.GetValue<int?>("Rental:Deduction:OverCapHoldDays") ?? 7;
        if (windowHours is < 1 or > 168 || extensionHours is < 0 or > 72 ||
            holdDays is < 1 or > 365 || holdDays * 24 <= windowHours + extensionHours)
            throw new InvalidOperationException("Rental:Deduction window, extension and hold configuration is invalid.");

        // Giỏ: evaluator dùng chung quy tắc, use case ghi và read model đọc.
        services.AddScoped<RentalCartItemEvaluator>();
        services.AddScoped<RentalCartUseCases>();
        services.AddScoped<GetRentalCart>();
        // Giới hạn kỳ thuê và adapter đọc listing/availability từ Inventory.
        services.AddSingleton<IRentalCartPolicy, RentalCartOptions>();
        services.AddScoped<InventoryRentalCartReader>();
        services.AddScoped<IRentalListingReader>(sp => sp.GetRequiredService<InventoryRentalCartReader>());
        services.AddScoped<IRentalAvailabilityReader>(sp => sp.GetRequiredService<InventoryRentalCartReader>());

        // Client HTTP gọi service ngoài; tắt tự chuyển hướng ở các đăng ký này.
        services.AddHttpClient<IRentalMediaClient, HttpRentalMediaClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpClient<IPaymentRentalClient, HttpPaymentRentalClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpClient<IInventoryRentalClient, HttpInventoryRentalClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpClient<IAuthRentalClient, HttpAuthRentalClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpClient<IReviewRentalClient, HttpReviewRentalClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddScoped<IOrderReviewStatusEnricher, OrderReviewStatusEnricher>();

        // Checkout chia thành chuẩn bị, điều phối, ghi đơn và tạo hóa đơn.
        services.AddScoped<ICheckoutPreparationService, CheckoutPreparationService>();
        services.AddScoped<ICheckoutOrderWriter, CheckoutOrderWriter>();
        services.AddScoped<ICheckoutInvoiceIssuer, CheckoutInvoiceIssuer>();
        services.AddScoped<ICheckoutService, CheckoutService>();

        // Truy vấn/thao tác đơn theo quyền renter, supplier hoặc Admin.
        services.AddScoped<IRenterOrderQueryService, RenterOrderQueryService>();
        services.AddScoped<IRenterOrderCancellationService, RenterOrderCancellationService>();
        services.AddScoped<ISupplierSubOrderQueryService, SupplierSubOrderQueryService>();
        services.AddScoped<IAdminOrderQueryService, AdminOrderQueryService>();
        services.AddScoped<IRenterDeliveryNoteService, RenterDeliveryNoteService>();

        // Trả máy: request, vấn đề, Admin xử lý và ghi nhận trả dùng cùng ReturnService.
        services.AddScoped<IReturnService, ReturnService>();

        // Truy vấn lịch thuê cá nhân.
        services.AddScoped<IRentalCalendarService, RentalCalendarService>();

        // Sổ địa chỉ giao hàng của renter.
        services.AddScoped<IRenterAddressService, RenterAddressService>();

        // Sổ tài khoản hoàn tiền của renter và hàng đợi hoàn tiền cho Admin.
        services.AddScoped<IRenterBankAccountService, RenterBankAccountService>();
        services.AddScoped<IAdminSupplierBankAccountService, AdminSupplierBankAccountService>();
        services.AddScoped<IAdminRefundQueryService, AdminRefundQueryService>();
        services.AddScoped<IAdminRefundCommandService, AdminRefundCommandService>();

        // Executor điều phối receipt, side effect và recovery của các use case.
        services.AddScoped<IRentalOperationExecutor, RentalOperationExecutor>();

        // Áp dụng sự kiện thanh toán vào nghiệp vụ Rental.
        services.AddScoped<IPaymentWorkflowService, PaymentWorkflowService>();

        // Supplier quyết định đơn con; Admin truy vấn thống kê xác nhận.
        services.AddScoped<ISupplierDecisionService, SupplierDecisionService>();
        services.AddScoped<ISupplierStatisticsService, SupplierStatisticsService>();

        // Renter xác nhận nhận máy hoặc yêu cầu hủy item chưa bàn giao.
        services.AddScoped<IHandoverAcknowledgementService, HandoverAcknowledgementService>();
        services.AddScoped<IItemCancellationService, ItemCancellationService>();

        // Service kiểm tra và xử lý các mốc hết hạn thanh toán/supplier.
        services.AddScoped<IExpiryWorkflowService, ExpiryWorkflowService>();

        // Handler áp dụng kết quả hoàn tiền và quyết toán từ Payment.
        services.AddScoped<IRefundCompletedConsumer, RefundCompletedConsumer>();
        services.AddScoped<IRentalSettledConsumer, RentalSettledConsumer>();

        // Dịch vụ tích hợp ReviewService (snapshot và đối soát)
        services.AddScoped<Dprental.RentalService.Application.IServices.ReviewIntegration.IRentalReviewIntegrationService, Dprental.RentalService.Application.Services.ReviewIntegration.RentalReviewIntegrationService>();
        // Dịch vụ quản lý vòng đời lịch nhắc nhở bàn giao, trả máy và cảnh báo quá hạn.
        services.AddScoped<Dprental.RentalService.Application.IServices.Reminders.IRentalReminderScheduleService, Dprental.RentalService.Application.Services.Reminders.RentalReminderScheduleService>();
        services.AddScoped<Dprental.RentalService.Application.IServices.Reminders.IRentalReminderBatchProcessor, Dprental.RentalService.Application.Services.Reminders.RentalReminderBatchProcessor>();
        services.AddScoped<Dprental.RentalService.Application.IServices.Reminders.IRentalReminderBackfillService>(sp =>
            new Dprental.RentalService.Application.Services.Reminders.RentalReminderBackfillService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<Dprental.RentalService.Domain.IRepositories.IOrderLockManager>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Dprental.RentalService.Application.Services.Reminders.RentalReminderBackfillService>>()));
        services.AddOptions<Dprental.RentalService.Application.Options.RentalReminderOptions>()
            .BindConfiguration(Dprental.RentalService.Application.Options.RentalReminderOptions.SectionName);
        services.AddHostedService<RentalReminderWorker>();

        // Đăng ký pipeline Kafka: options, producers, dispatcher và ba worker Inbox/Outbox/broker.
        services.AddKafka(configuration ?? new ConfigurationBuilder().Build());

        // Worker theo lịch: tạo scope và kích hoạt service/executor; không phải consumer Kafka.
        services.AddHostedService<OperationRecoveryWorker>();
        services.AddHostedService<ExpiryScannerWorker>();
        services.AddHostedService<OverdueProtectionWorker>();
        services.AddHostedService<CheckoutTimeoutScannerWorker>();

        // Metrics dùng chung và nguồn thời gian được inject cho nghiệp vụ/worker.
        services.AddSingleton<RentalMetrics>();
        services.AddSingleton(TimeProvider.System);

        // Cấu hình và service bảo vệ thiết bị thuê quá hạn.
        services.AddOptions<OverdueProtectionOptions>()
            .BindConfiguration(OverdueProtectionOptions.SectionName);
        services.AddScoped<IOverdueProtectionService, OverdueProtectionService>();

        // Quét bàn giao quá hạn; kiểm tra giới hạn giờ gia hạn ngay lúc startup.
        services.AddOptions<HandoverOverdueOptions>()
            .BindConfiguration(HandoverOverdueOptions.SectionName)
            .Validate(o => o.AutoExpireGraceHours is >= 1 and <= 168,
                "Rental:HandoverOverdue:AutoExpireGraceHours must be between 1 and 168.")
            .ValidateOnStart();
        services.AddScoped<IHandoverOverdueService, HandoverOverdueService>();
        services.AddHostedService<HandoverOverdueWorker>();

        // Đề xuất, phản hồi và giải quyết tranh chấp khấu trừ để lập lịch quyết toán.
        services.AddScoped<IDeductionService, DeductionService>();
        services.AddScoped<IDeductionResponseService, DeductionResponseService>();
        services.AddScoped<IDamageLevelService, DamageLevelService>();
        // Worker lần lượt xử lý deadline khấu trừ và lập lịch quyết toán.
        services.AddHostedService<DeductionDeadlineWorker>();
        services.AddHostedService<RefundReconciliationWorker>();

        // Worker kích hoạt xử lý yêu cầu trả máy quá hạn xác nhận.
        services.AddScoped<IReturnConfirmationOverdueService, ReturnConfirmationOverdueService>();
        services.AddHostedService<ReturnConfirmationOverdueWorker>();

        return services;
    }
}
