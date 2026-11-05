using Dprental.NotificationService.Application.Policies;
using Dprental.NotificationService.Application.Recipients;
using Dprental.NotificationService.Application.Templates;
using Dprental.NotificationService.Domain.Entities;
using Xunit;

namespace Dprental.NotificationService.Api.Tests.Policies;

[Trait("Category", "Unit")]
public sealed class NotificationPolicyAndTemplateTests
{
    private readonly RecipientResolutionService _recipientResolver = new();
    private readonly NotificationTemplateRenderer _renderer = new();

    [Fact]
    public void RecipientResolution_ZeroInference_EmptyGuidMustFail()
    {
        var orderId = Guid.NewGuid();
        var result = _recipientResolver.ResolveRecipients(
            "RentalOrderCreated",
            orderId,
            renterId: Guid.Empty,
            supplierId: null);

        Assert.True(result.IsFailure);
        Assert.Equal("MISSING_RECIPIENT", result.Error.Code);
    }

    [Fact]
    public void RecipientResolution_DualRecipients_ShouldReturnBothSupplierAndRenter()
    {
        var orderId = Guid.NewGuid();
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var result = _recipientResolver.ResolveRecipients(
            "RentalHandoverOverdue",
            orderId,
            renterId: renterId,
            supplierId: supplierId);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Count);

        var supplier = result.Value.First(r => r.RecipientRole == "Supplier");
        Assert.Equal("HANDOVER_OVERDUE_SUPPLIER", supplier.NotificationType);
        Assert.True(supplier.IsActionable);

        var renter = result.Value.First(r => r.RecipientRole == "Renter");
        Assert.Equal("HANDOVER_OVERDUE_RENTER", renter.NotificationType);
        Assert.False(renter.IsActionable);
    }

    [Fact]
    public void NotificationChannelPolicy_ShouldOnlyEnableEmailForAllowedTypesAndRoles()
    {
        Assert.False(NotificationChannelPolicy.ShouldSendEmail("ORDER_CREATED", "Renter"));
        Assert.True(NotificationChannelPolicy.ShouldSendEmail("ORDER_PAID", "Renter"));
        Assert.True(NotificationChannelPolicy.ShouldSendEmail("SUPPLIER_CONFIRMATION_REQUIRED", "Supplier"));
        Assert.False(NotificationChannelPolicy.ShouldSendEmail("ORDER_EXPIRED", "Renter"));
        Assert.False(NotificationChannelPolicy.ShouldSendEmail("ORDER_CANCELLED", "Renter"));
        Assert.False(NotificationChannelPolicy.ShouldSendEmail("HANDOVER_COMPLETED", "Renter"));
        Assert.True(NotificationChannelPolicy.ShouldSendEmail("HANDOVER_OVERDUE_SUPPLIER", "Supplier"));
        Assert.False(NotificationChannelPolicy.ShouldSendEmail("HANDOVER_OVERDUE_RENTER", "Renter"));
        Assert.True(NotificationChannelPolicy.ShouldSendEmail("ORDER_COMPLETED", "Renter"));
        Assert.False(NotificationChannelPolicy.ShouldSendEmail("LATE_PAYMENT_REFUND_REQUESTED", "Renter"));
        Assert.True(NotificationChannelPolicy.ShouldSendEmail("REFUND_COMPLETED", "Renter"));
        Assert.False(NotificationChannelPolicy.ShouldSendEmail("REFUND_COMPLETED", "Supplier"));
        Assert.True(NotificationChannelPolicy.ShouldSendEmail("PAYOUT_COMPLETED", "Supplier"));
        Assert.False(NotificationChannelPolicy.ShouldSendEmail("PAYOUT_COMPLETED", "Renter"));
        Assert.False(NotificationChannelPolicy.ShouldSendEmail("ORDER_CONFIRMED", "Renter"));
        Assert.False(NotificationChannelPolicy.ShouldSendEmail("ORDER_REJECTED", "Renter"));
    }

    [Fact]
    public void StaleActionSuppressionPolicy_ShouldSuppressActionIfOrderIsTerminal()
    {
        var watermark = new NotificationAggregateWatermark
        {
            AggregateId = Guid.NewGuid(),
            RecipientUserId = Guid.NewGuid(),
            LastOrderVersion = 2,
            IsTerminal = true,
            TerminalState = "PAID"
        };

        var eval = StaleActionSuppressionPolicy.Evaluate("ORDER_CREATED", incomingOrderVersion: 1, watermark);
        Assert.True(eval.IsStale);
        Assert.True(eval.SuppressAction);
        Assert.True(eval.SuppressEmail);
    }

    [Fact]
    public void StaleActionSuppressionPolicy_PaidMilestone_SuppressesPaymentCTA_ButDoesNotBlockSupplierConfirmation()
    {
        var watermark = new NotificationAggregateWatermark
        {
            AggregateId = Guid.NewGuid(),
            RecipientUserId = Guid.NewGuid(),
            LastOrderVersion = 2,
            IsTerminal = false,
            IsPaid = true
        };

        // ORDER_CREATED (payment CTA) is suppressed because order is already paid
        var orderCreatedEval = StaleActionSuppressionPolicy.Evaluate("ORDER_CREATED", incomingOrderVersion: 1, watermark);
        Assert.True(orderCreatedEval.IsStale);
        Assert.True(orderCreatedEval.SuppressAction);

        // SUPPLIER_CONFIRMATION_REQUIRED is NOT blocked by PAID milestone
        var supplierEval = StaleActionSuppressionPolicy.Evaluate("SUPPLIER_CONFIRMATION_REQUIRED", incomingOrderVersion: 2, watermark);
        Assert.False(supplierEval.SuppressAction);
        Assert.False(supplierEval.IsStale);
    }

    [Fact]
    public void StaleActionSuppressionPolicy_TerminalOrder_SuppressesSupplierConfirmationAndHandover()
    {
        var watermark = new NotificationAggregateWatermark
        {
            AggregateId = Guid.NewGuid(),
            RecipientUserId = Guid.NewGuid(),
            LastOrderVersion = 2,
            IsTerminal = true,
            TerminalState = "EXPIRED"
        };

        var supplierEval = StaleActionSuppressionPolicy.Evaluate("SUPPLIER_CONFIRMATION_REQUIRED", incomingOrderVersion: 2, watermark);
        Assert.True(supplierEval.IsStale);
        Assert.True(supplierEval.SuppressAction);
        Assert.True(supplierEval.SuppressEmail);

        var handoverEval = StaleActionSuppressionPolicy.Evaluate("HANDOVER_OVERDUE_SUPPLIER", incomingOrderVersion: 2, watermark);
        Assert.True(handoverEval.IsStale);
        Assert.True(handoverEval.SuppressAction);
        Assert.True(handoverEval.SuppressEmail);
    }

    [Fact]
    public void StaleActionSuppressionPolicy_OrderCompleted_IsNotSuppressedWhenOrderIsCompleted()
    {
        var watermark = new NotificationAggregateWatermark
        {
            AggregateId = Guid.NewGuid(),
            RecipientUserId = Guid.NewGuid(),
            LastOrderVersion = 5,
            IsTerminal = true,
            TerminalState = "COMPLETED"
        };

        var eval = StaleActionSuppressionPolicy.Evaluate("ORDER_COMPLETED", incomingOrderVersion: 5, watermark);
        Assert.False(eval.IsStale);
        Assert.False(eval.SuppressAction);
        Assert.False(eval.SuppressEmail);
    }

    [Fact]
    public void StaleActionSuppressionPolicy_RefundCompleted_IsNotSuppressedWhenOrderIsCancelled()
    {
        var watermark = new NotificationAggregateWatermark
        {
            AggregateId = Guid.NewGuid(),
            RecipientUserId = Guid.NewGuid(),
            LastOrderVersion = 4,
            IsTerminal = true,
            TerminalState = "CANCELLED"
        };

        var eval = StaleActionSuppressionPolicy.Evaluate("REFUND_COMPLETED", incomingOrderVersion: 4, watermark);
        Assert.False(eval.IsStale);
        Assert.False(eval.SuppressAction);
        Assert.False(eval.SuppressEmail);
    }

    [Fact]
    public void StaleActionSuppressionPolicy_SupplierConfirmationRequired_IsSuppressedWhenOrderIsCancelled()
    {
        var watermark = new NotificationAggregateWatermark
        {
            AggregateId = Guid.NewGuid(),
            RecipientUserId = Guid.NewGuid(),
            LastOrderVersion = 3,
            IsTerminal = true,
            TerminalState = "CANCELLED"
        };

        var eval = StaleActionSuppressionPolicy.Evaluate("SUPPLIER_CONFIRMATION_REQUIRED", incomingOrderVersion: 2, watermark);
        Assert.True(eval.IsStale);
        Assert.True(eval.SuppressAction);
        Assert.True(eval.SuppressEmail);
    }

    [Fact]
    public void StaleActionSuppressionPolicy_PayoutCompleted_IsNotSuppressedWhenWatermarkIsTerminal()
    {
        var watermark = new NotificationAggregateWatermark
        {
            AggregateId = Guid.NewGuid(),
            RecipientUserId = Guid.NewGuid(),
            LastOrderVersion = 4,
            IsTerminal = true,
            TerminalState = "COMPLETED"
        };

        var eval = StaleActionSuppressionPolicy.Evaluate("PAYOUT_COMPLETED", incomingOrderVersion: 4, watermark);
        Assert.False(eval.IsStale);
        Assert.False(eval.SuppressAction);
        Assert.False(eval.SuppressEmail);
    }

    [Fact]
    public void StaleActionSuppressionPolicy_OrderPaid_IsNotSuppressedWhenWatermarkIsTerminal()
    {
        var watermark = new NotificationAggregateWatermark
        {
            AggregateId = Guid.NewGuid(),
            RecipientUserId = Guid.NewGuid(),
            LastOrderVersion = 5,
            IsTerminal = true,
            TerminalState = "COMPLETED"
        };

        var eval = StaleActionSuppressionPolicy.Evaluate("ORDER_PAID", incomingOrderVersion: 5, watermark);
        Assert.False(eval.IsStale);
        Assert.False(eval.SuppressAction);
        Assert.False(eval.SuppressEmail);
    }

    [Fact]
    public void StaleActionSuppressionPolicy_OrderCancelled_WithOnlyRenterWatermark_SuppressesSupplierEvents()
    {
        var orderId = Guid.NewGuid();
        // Order aggregate state projected from renter's CANCELLED watermark
        var orderAggregateState = new AggregateStateProjection(
            Scope: NotificationWatermarkScope.RentalOrder,
            AggregateId: orderId,
            MaxVersion: 3,
            IsPaid: true,
            IsTerminal: true,
            TerminalState: "CANCELLED",
            TerminalVersion: 3);

        // Late supplier confirmation required must be suppressed
        var supplierConfirmEval = StaleActionSuppressionPolicy.Evaluate(
            "SUPPLIER_CONFIRMATION_REQUIRED",
            incomingOrderVersion: 2,
            orderAggregateState);
        Assert.True(supplierConfirmEval.IsStale);
        Assert.True(supplierConfirmEval.SuppressAction);
        Assert.True(supplierConfirmEval.SuppressEmail);

        // Late handover overdue supplier must be suppressed
        var handoverOverdueEval = StaleActionSuppressionPolicy.Evaluate(
            "HANDOVER_OVERDUE_SUPPLIER",
            incomingOrderVersion: 2,
            orderAggregateState);
        Assert.True(handoverOverdueEval.IsStale);
        Assert.True(handoverOverdueEval.SuppressAction);
        Assert.True(handoverOverdueEval.SuppressEmail);
    }

    [Fact]
    public void StaleActionSuppressionPolicy_ItemCompleted_WithOnlyRenterWatermark_SuppressesLateSupplierNotification()
    {
        var itemId = Guid.NewGuid();
        // Item aggregate state projected from renter's COMPLETED watermark
        var itemAggregateState = new AggregateStateProjection(
            Scope: NotificationWatermarkScope.RentalItem,
            AggregateId: itemId,
            MaxVersion: 4,
            IsPaid: false,
            IsTerminal: true,
            TerminalState: "COMPLETED",
            TerminalVersion: 4);

        // Late supplier notification on completed item must be suppressed
        var supplierEval = StaleActionSuppressionPolicy.Evaluate(
            "HANDOVER_OVERDUE_SUPPLIER",
            incomingOrderVersion: 3,
            itemAggregateState);
        Assert.True(supplierEval.IsStale);
        Assert.True(supplierEval.SuppressAction);
        Assert.True(supplierEval.SuppressEmail);
    }

    [Fact]
    public void StaleActionSuppressionPolicy_TwoSuppliersOnSameOrder_ShareOrderAggregateState_IndependentItemAggregateStates()
    {
        var orderId = Guid.NewGuid();
        var itemAId = Guid.NewGuid();
        var itemBId = Guid.NewGuid();

        // Active order aggregate state (version 5, paid, not terminal)
        var orderAggregateState = new AggregateStateProjection(
            Scope: NotificationWatermarkScope.RentalOrder,
            AggregateId: orderId,
            MaxVersion: 5,
            IsPaid: true,
            IsTerminal: false,
            TerminalState: null,
            TerminalVersion: null);

        // Supplier A's item is COMPLETED (terminal)
        var itemAState = new AggregateStateProjection(
            Scope: NotificationWatermarkScope.RentalItem,
            AggregateId: itemAId,
            MaxVersion: 4,
            IsPaid: false,
            IsTerminal: true,
            TerminalState: "COMPLETED",
            TerminalVersion: 4);

        // Supplier B's item is still active (not terminal)
        var itemBState = new AggregateStateProjection(
            Scope: NotificationWatermarkScope.RentalItem,
            AggregateId: itemBId,
            MaxVersion: 2,
            IsPaid: false,
            IsTerminal: false,
            TerminalState: null,
            TerminalVersion: null);

        // Item A actionable notification is suppressed because Item A is completed
        var itemAEval = StaleActionSuppressionPolicy.Evaluate(
            "HANDOVER_OVERDUE_SUPPLIER",
            incomingOrderVersion: 4,
            itemAState);
        Assert.True(itemAEval.SuppressAction);
        Assert.True(itemAEval.SuppressEmail);

        // Item B actionable notification is NOT suppressed because Item B is active and Order is active
        var itemBEval = StaleActionSuppressionPolicy.Evaluate(
            "HANDOVER_OVERDUE_SUPPLIER",
            incomingOrderVersion: 2,
            itemBState);
        Assert.False(itemBEval.SuppressAction);
        Assert.False(itemBEval.SuppressEmail);

        // Now Order is cancelled:
        var cancelledOrderState = orderAggregateState with
        {
            IsTerminal = true,
            TerminalState = "CANCELLED",
            TerminalVersion = 6,
            MaxVersion = 6
        };

        // Because order aggregate state is CANCELLED, both suppliers are suppressed at the order level!
        var supplierAOrderEval = StaleActionSuppressionPolicy.Evaluate(
            "SUPPLIER_CONFIRMATION_REQUIRED",
            incomingOrderVersion: 2,
            cancelledOrderState);
        var supplierBOrderEval = StaleActionSuppressionPolicy.Evaluate(
            "SUPPLIER_CONFIRMATION_REQUIRED",
            incomingOrderVersion: 2,
            cancelledOrderState);

        Assert.True(supplierAOrderEval.SuppressAction);
        Assert.True(supplierBOrderEval.SuppressAction);
    }

    [Fact]
    public void TemplateRenderer_MissingOrderCode_ShouldFallbackToResourceId()
    {
        var orderId = Guid.NewGuid();
        var data = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId,
            ["totalPayableAmount"] = 1500000m,
            ["paymentDeadline"] = "2026-11-02T10:30:00Z"
        };

        var result = _renderer.Render("ORDER_CREATED", data);
        Assert.True(result.IsSuccess);
        Assert.Contains(orderId.ToString(), result.Value.Title);
        Assert.Contains("1.500.000", result.Value.Body);
    }

    [Fact]
    public void TemplateRenderer_HtmlInjection_ShouldEscapeHtml()
    {
        var data = new Dictionary<string, object?>
        {
            ["orderCode"] = "<script>alert('xss')</script>",
            ["paidAmount"] = 500000m,
            ["invoiceRef"] = "INV-001"
        };

        var result = _renderer.Render("ORDER_PAID", data);
        Assert.True(result.IsSuccess);
        Assert.DoesNotContain("<script>", result.Value.Title);
        Assert.Contains("&lt;script&gt;", result.Value.Title);
    }

    [Fact]
    public void TemplateRenderer_FinancialWordingInvariant_LatePaymentRefundRequested()
    {
        var data = new Dictionary<string, object?>
        {
            ["orderCode"] = "RNT-20261102-0001",
            ["totalRefundAmount"] = 2000000m
        };

        var result = _renderer.Render("LATE_PAYMENT_REFUND_REQUESTED", data);
        Assert.True(result.IsSuccess);

        // MUST state requested / processing
        Assert.Contains("đang được xử lý hoàn tiền", result.Value.Body);

        // MUST NEVER claim refund or settlement completed
        Assert.DoesNotContain("Đã hoàn tiền thành công", result.Value.Body);
        Assert.DoesNotContain("hoàn tất hoàn tiền", result.Value.Body);
        Assert.DoesNotContain("đã chuyển tiền", result.Value.Body);
    }

    [Fact]
    public void RecipientResolution_NewLifecycleAndPaymentEvents_ShouldResolveCorrectly()
    {
        var orderId = Guid.NewGuid();
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var settlementId = Guid.NewGuid();

        // RentalItemConfirmed -> Renter (Category ORDER, Severity INFO, NotificationType ORDER_CONFIRMED)
        var confirmed = _recipientResolver.ResolveRecipients("RentalItemConfirmed", orderId, renterId, supplierId);
        Assert.True(confirmed.IsSuccess);
        Assert.Single(confirmed.Value);
        var confirmedRecipient = confirmed.Value[0];
        Assert.Equal(renterId, confirmedRecipient.RecipientUserId);
        Assert.Equal("Renter", confirmedRecipient.RecipientRole);
        Assert.Equal("ORDER_CONFIRMED", confirmedRecipient.NotificationType);
        Assert.Equal("ORDER", confirmedRecipient.Category);
        Assert.Equal("INFO", confirmedRecipient.Severity);
        Assert.False(confirmedRecipient.IsActionable);
        Assert.Equal($"/orders/{orderId}", confirmedRecipient.DeepLinkUrl);

        // RentalItemRejected -> Renter (Category ORDER, Severity WARNING, NotificationType ORDER_REJECTED)
        var rejected = _recipientResolver.ResolveRecipients("RentalItemRejected", orderId, renterId, supplierId);
        Assert.True(rejected.IsSuccess);
        Assert.Single(rejected.Value);
        var rejectedRecipient = rejected.Value[0];
        Assert.Equal(renterId, rejectedRecipient.RecipientUserId);
        Assert.Equal("Renter", rejectedRecipient.RecipientRole);
        Assert.Equal("ORDER_REJECTED", rejectedRecipient.NotificationType);
        Assert.Equal("ORDER", rejectedRecipient.Category);
        Assert.Equal("WARNING", rejectedRecipient.Severity);
        Assert.False(rejectedRecipient.IsActionable);
        Assert.Equal($"/orders/{orderId}", rejectedRecipient.DeepLinkUrl);

        // RefundCompletedFinancial -> Renter (Category PAYMENT, Severity SUCCESS, NotificationType REFUND_COMPLETED)
        var refund = _recipientResolver.ResolveRecipients("RefundCompletedFinancial", orderId, renterId, null);
        Assert.True(refund.IsSuccess);
        Assert.Single(refund.Value);
        var refundRecipient = refund.Value[0];
        Assert.Equal(renterId, refundRecipient.RecipientUserId);
        Assert.Equal("Renter", refundRecipient.RecipientRole);
        Assert.Equal("REFUND_COMPLETED", refundRecipient.NotificationType);
        Assert.Equal("PAYMENT", refundRecipient.Category);
        Assert.Equal("SUCCESS", refundRecipient.Severity);
        Assert.False(refundRecipient.IsActionable);
        Assert.Equal($"/orders/{orderId}", refundRecipient.DeepLinkUrl);

        // SupplierPayoutCompleted -> Supplier (Category PAYMENT, Severity SUCCESS, NotificationType PAYOUT_COMPLETED)
        var payout = _recipientResolver.ResolveRecipients("SupplierPayoutCompleted", settlementId, null, supplierId);
        Assert.True(payout.IsSuccess);
        Assert.Single(payout.Value);
        var payoutRecipient = payout.Value[0];
        Assert.Equal(supplierId, payoutRecipient.RecipientUserId);
        Assert.Equal("Supplier", payoutRecipient.RecipientRole);
        Assert.Equal("PAYOUT_COMPLETED", payoutRecipient.NotificationType);
        Assert.Equal("PAYMENT", payoutRecipient.Category);
        Assert.Equal("SUCCESS", payoutRecipient.Severity);
        Assert.False(payoutRecipient.IsActionable);
        Assert.Equal("/own/orders", payoutRecipient.DeepLinkUrl);
    }

    [Fact]
    public void TemplateRenderer_OrderConfirmed_RendersVietnameseAndDeepLink()
    {
        var orderId = Guid.NewGuid();
        var data = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId,
            ["orderCode"] = "RNT-20261104-0001",
            ["inventoryConfirmationRef"] = "INV-CONF-999"
        };

        var result = _renderer.Render("ORDER_CONFIRMED", data);
        Assert.True(result.IsSuccess);
        Assert.Contains("RNT-20261104-0001", result.Value.Title);
        Assert.Contains("INV-CONF-999", result.Value.Body);
        Assert.Contains("xác nhận", result.Value.Title.ToLowerInvariant());
        Assert.NotNull(result.Value.EmailBody);
        Assert.Contains($"/orders/{orderId}", result.Value.EmailBody);
    }

    [Fact]
    public void TemplateRenderer_OrderRejected_RendersVietnameseAndDeepLink()
    {
        var orderId = Guid.NewGuid();
        var data = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId,
            ["orderCode"] = "RNT-20261104-0002",
            ["reason"] = "Thiết bị đang bảo trì khẩn cấp"
        };

        var result = _renderer.Render("ORDER_REJECTED", data);
        Assert.True(result.IsSuccess);
        Assert.Contains("RNT-20261104-0002", result.Value.Title);
        Assert.Contains("từ chối", result.Value.Title.ToLowerInvariant());
        Assert.Contains(System.Net.WebUtility.HtmlEncode("Thiết bị đang bảo trì khẩn cấp"), result.Value.Body);
        Assert.NotNull(result.Value.EmailBody);
        Assert.Contains($"/orders/{orderId}", result.Value.EmailBody);
    }

    [Fact]
    public void TemplateRenderer_RentalItemCancelled_DescribesItemOnly_DoesNotClaimEntireOrderCancelled()
    {
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var data = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId,
            ["rentalItemId"] = itemId,
            ["orderCode"] = "RNT-20261104-0005",
            ["reason"] = "Nhà cung cấp hết phụ kiện đi kèm"
        };

        var result = _renderer.Render("RENTAL_ITEM_CANCELLED", data);
        Assert.True(result.IsSuccess);
        Assert.Contains("RNT-20261104-0005", result.Value.Title);
        Assert.Contains("Thiết bị trong đơn thuê", result.Value.Title);
        Assert.Contains(itemId.ToString(), result.Value.Body);
        Assert.Contains("Các phần thuê khác trong đơn (nếu có) vẫn tiếp tục được xử lý", result.Value.Body);
        Assert.NotNull(result.Value.EmailBody);
        Assert.Contains($"/orders/{orderId}", result.Value.EmailBody);
    }

    [Fact]
    public void TemplateRenderer_RentalItemExpired_DescribesItemOnly_DoesNotClaimEntireOrderExpired()
    {
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var data = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId,
            ["rentalItemId"] = itemId,
            ["orderCode"] = "RNT-20261104-0006",
            ["reason"] = "Hết hạn thanh toán cọc thiết bị"
        };

        var result = _renderer.Render("RENTAL_ITEM_EXPIRED", data);
        Assert.True(result.IsSuccess);
        Assert.Contains("RNT-20261104-0006", result.Value.Title);
        Assert.Contains("Thiết bị trong đơn thuê", result.Value.Title);
        Assert.Contains(itemId.ToString(), result.Value.Body);
        Assert.Contains("Thiết bị đã được giải phóng", result.Value.Body);
        Assert.NotNull(result.Value.EmailBody);
        Assert.Contains($"/orders/{orderId}", result.Value.EmailBody);
    }

    [Fact]
    public void TemplateRenderer_RefundCompleted_FormatsAmountAndDeepLink()
    {
        var orderId = Guid.NewGuid();
        var data = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId,
            ["orderCode"] = "RNT-20261104-0003",
            ["amount"] = 1500000m,
            ["currency"] = "VND",
            ["refundRef"] = "REF-12345678",
            ["refundKind"] = "FULL_CANCELLATION",
            ["completedAt"] = "2026-11-04T09:00:00.0000000+00:00"
        };

        var result = _renderer.Render("REFUND_COMPLETED", data);
        Assert.True(result.IsSuccess);
        Assert.Contains("1.500.000", result.Value.Body);
        Assert.Contains("REF-12345678", result.Value.Body);
        Assert.Contains("hoàn tiền thành công", result.Value.Title.ToLowerInvariant());
        Assert.NotNull(result.Value.EmailBody);
        Assert.Contains("1.500.000", result.Value.EmailBody);
        Assert.Contains($"/orders/{orderId}", result.Value.EmailBody);
    }

    [Fact]
    public void TemplateRenderer_PayoutCompleted_FormatsAmountAndDeepLink()
    {
        var settlementId = Guid.NewGuid();
        var data = new Dictionary<string, object?>
        {
            ["settlementId"] = settlementId,
            ["payoutRef"] = "PAYOUT-87654321",
            ["amount"] = 900000m,
            ["currency"] = "VND",
            ["completedAt"] = "2026-11-04T12:00:00.0000000+00:00"
        };

        var result = _renderer.Render("PAYOUT_COMPLETED", data);
        Assert.True(result.IsSuccess);
        Assert.Contains("PAYOUT-87654321", result.Value.Title);
        Assert.Contains("900.000", result.Value.Body);
        Assert.Contains("doanh thu", result.Value.Body.ToLowerInvariant());
        Assert.NotNull(result.Value.EmailBody);
        Assert.Contains("900.000", result.Value.EmailBody);
        Assert.Contains("/own/orders", result.Value.EmailBody);
    }

    [Fact]
    public void RecipientResolution_FiveMissingRentalLifecycleEvents_ShouldMatchAcceptanceCriteria()
    {
        var orderId = Guid.NewGuid();
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var returnRequestId = Guid.NewGuid();

        // 1. RentalOrderRejected: Renter, Category ORDER, Severity WARNING, NotificationType ORDER_REJECTED
        var rejected = _recipientResolver.ResolveRecipients("RentalOrderRejected", orderId, renterId, supplierId);
        Assert.True(rejected.IsSuccess);
        Assert.Single(rejected.Value);
        var rj = rejected.Value[0];
        Assert.Equal(renterId, rj.RecipientUserId);
        Assert.Equal("Renter", rj.RecipientRole);
        Assert.Equal("ORDER_REJECTED", rj.NotificationType);
        Assert.Equal("ORDER", rj.Category);
        Assert.Equal("WARNING", rj.Severity);

        // 2. RentalItemExpired: Renter, Category ORDER, Severity WARNING, NotificationType ORDER_EXPIRED, item scope
        var expired = _recipientResolver.ResolveRecipients("RentalItemExpired", orderId, renterId, supplierId);
        Assert.True(expired.IsSuccess);
        Assert.Single(expired.Value);
        var exp = expired.Value[0];
        Assert.Equal(renterId, exp.RecipientUserId);
        Assert.Equal("Renter", exp.RecipientRole);
        Assert.Equal("RENTAL_ITEM_EXPIRED", exp.NotificationType);
        Assert.Equal("ORDER", exp.Category);
        Assert.Equal("WARNING", exp.Severity);

        // 3. RentalItemCancelled: Renter, Category ORDER, Severity WARNING, NotificationType ORDER_CANCELLED, item scope
        var cancelled = _recipientResolver.ResolveRecipients("RentalItemCancelled", orderId, renterId, supplierId);
        Assert.True(cancelled.IsSuccess);
        Assert.Single(cancelled.Value);
        var can = cancelled.Value[0];
        Assert.Equal(renterId, can.RecipientUserId);
        Assert.Equal("Renter", can.RecipientRole);
        Assert.Equal("RENTAL_ITEM_CANCELLED", can.NotificationType);
        Assert.Equal("ORDER", can.Category);
        Assert.Equal("WARNING", can.Severity);

        // 4. RentalItemReturned: Renter & Supplier, Category RETURN, Severity INFO, NotificationType RETURN_CONFIRMED
        var returned = _recipientResolver.ResolveRecipients("RentalItemReturned", orderId, renterId, supplierId, returnRequestId);
        Assert.True(returned.IsSuccess);
        Assert.Equal(2, returned.Value.Count);
        var retRenter = returned.Value.First(r => r.RecipientRole == "Renter");
        Assert.Equal(renterId, retRenter.RecipientUserId);
        Assert.Equal("RETURN_CONFIRMED", retRenter.NotificationType);
        Assert.Equal("RETURN", retRenter.Category);
        Assert.Equal("INFO", retRenter.Severity);
        var retSupplier = returned.Value.First(r => r.RecipientRole == "Supplier");
        Assert.Equal(supplierId, retSupplier.RecipientUserId);
        Assert.Equal("RETURN_CONFIRMED", retSupplier.NotificationType);
        Assert.Equal("RETURN", retSupplier.Category);
        Assert.Equal("INFO", retSupplier.Severity);

        // 5. RentalItemCompleted: Renter & Supplier, Category ORDER, Severity SUCCESS, NotificationType RENTAL_COMPLETED
        var completed = _recipientResolver.ResolveRecipients("RentalItemCompleted", orderId, renterId, supplierId);
        Assert.True(completed.IsSuccess);
        Assert.Equal(2, completed.Value.Count);
        var compRenter = completed.Value.First(r => r.RecipientRole == "Renter");
        Assert.Equal(renterId, compRenter.RecipientUserId);
        Assert.Equal("RENTAL_COMPLETED", compRenter.NotificationType);
        Assert.Equal("ORDER", compRenter.Category);
        Assert.Equal("SUCCESS", compRenter.Severity);
        var compSupplier = completed.Value.First(r => r.RecipientRole == "Supplier");
        Assert.Equal(supplierId, compSupplier.RecipientUserId);
        Assert.Equal("RENTAL_COMPLETED", compSupplier.NotificationType);
        Assert.Equal("ORDER", compSupplier.Category);
        Assert.Equal("SUCCESS", compSupplier.Severity);
    }

    [Fact]
    public void TemplateRenderer_MissingRentalLifecycleEvents_RendersVietnameseTemplates()
    {
        var orderId = Guid.NewGuid();
        var data = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId,
            ["orderCode"] = "RNT-20261104-0010"
        };

        // RETURN_CONFIRMED
        var returnResult = _renderer.Render("RETURN_CONFIRMED", data);
        Assert.True(returnResult.IsSuccess);
        Assert.Contains("hoàn trả", returnResult.Value.Title.ToLowerInvariant());
        Assert.Contains("xác nhận hoàn tất", returnResult.Value.Body.ToLowerInvariant());
        Assert.NotNull(returnResult.Value.EmailSubject);
        Assert.NotNull(returnResult.Value.EmailBody);

        // RENTAL_COMPLETED
        var completedResult = _renderer.Render("RENTAL_COMPLETED", data);
        Assert.True(completedResult.IsSuccess);
        Assert.Contains("hoàn tất", completedResult.Value.Title.ToLowerInvariant());
        Assert.Contains("chu kỳ thuê", completedResult.Value.Body.ToLowerInvariant());
        Assert.NotNull(completedResult.Value.EmailSubject);
        Assert.NotNull(completedResult.Value.EmailBody);
    }

    [Fact]
    public void StaleActionSuppressionPolicy_WhenPaidAndCancelled_ActionableNotificationsAreSuppressed()
    {
        var aggregateState = new AggregateStateProjection(
            Scope: NotificationWatermarkScope.RentalOrder,
            AggregateId: Guid.NewGuid(),
            MaxVersion: 3,
            IsPaid: true,
            IsTerminal: true,
            TerminalState: "CANCELLED",
            TerminalVersion: 3);

        var result = StaleActionSuppressionPolicy.Evaluate("SUPPLIER_CONFIRMATION_REQUIRED", incomingOrderVersion: 2, aggregateState);

        Assert.True(result.IsStale);
        Assert.True(result.SuppressAction);
        Assert.True(result.SuppressEmail);
        Assert.Contains("CANCELLED", result.Reason);
    }

    [Fact]
    public void StaleActionSuppressionPolicy_WhenParentOrderHasHigherVersionDueToAnotherItem_DoesNotSuppressCurrentItemAction()
    {
        var parentOrder = new AggregateStateProjection(
            Scope: NotificationWatermarkScope.RentalOrder,
            AggregateId: Guid.NewGuid(),
            MaxVersion: 5,
            IsPaid: true,
            IsTerminal: false,
            TerminalState: null,
            TerminalVersion: null);

        var currentItem = new AggregateStateProjection(
            Scope: NotificationWatermarkScope.RentalItem,
            AggregateId: Guid.NewGuid(),
            MaxVersion: 1,
            IsPaid: false,
            IsTerminal: false,
            TerminalState: null,
            TerminalVersion: null);

        var result = StaleActionSuppressionPolicy.EvaluateWithParentState(
            "HANDOVER_OVERDUE_SUPPLIER",
            incomingOrderVersion: 1,
            currentAggregateState: currentItem,
            parentItemAggregateState: null,
            parentOrderAggregateState: parentOrder);

        Assert.False(result.IsStale);
        Assert.False(result.SuppressAction);
        Assert.False(result.SuppressEmail);
    }

    [Fact]
    public void StandardizedNotificationLinks_RenterAndSupplierContracts_ShouldMatchSpecification()
    {
        var orderId = Guid.NewGuid();
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var returnReqId = Guid.NewGuid();

        // 1. RentalOrderCreated: Renter DeepLinkUrl = /orders/{orderId}, ActionUrl = /orders/{orderId}, IsActionable = true
        var created = _recipientResolver.ResolveRecipients("RentalOrderCreated", orderId, renterId, null);
        Assert.True(created.IsSuccess);
        var createdRenter = Assert.Single(created.Value);
        Assert.Equal($"/orders/{orderId}", createdRenter.DeepLinkUrl);
        Assert.Equal($"/orders/{orderId}", createdRenter.ActionUrl);
        Assert.True(createdRenter.IsActionable);

        // 2. RentalOrderPaid: Renter DeepLinkUrl = /orders/{orderId}, ActionUrl = null, IsActionable = false
        var paid = _recipientResolver.ResolveRecipients("RentalOrderPaid", orderId, renterId, null);
        Assert.True(paid.IsSuccess);
        var paidRenter = Assert.Single(paid.Value);
        Assert.Equal($"/orders/{orderId}", paidRenter.DeepLinkUrl);
        Assert.Null(paidRenter.ActionUrl);
        Assert.False(paidRenter.IsActionable);

        // 3. RentalSupplierConfirmationRequested: Supplier DeepLinkUrl = /own/orders, ActionUrl = /own/orders, IsActionable = true
        var supplierConf = _recipientResolver.ResolveRecipients("RentalSupplierConfirmationRequested", orderId, null, supplierId);
        Assert.True(supplierConf.IsSuccess);
        var confSupplier = Assert.Single(supplierConf.Value);
        Assert.Equal("/own/orders", confSupplier.DeepLinkUrl);
        Assert.Equal("/own/orders", confSupplier.ActionUrl);
        Assert.True(confSupplier.IsActionable);

        // 4. RentalHandoverOverdue: Supplier = /own/orders (Actionable), Renter = /orders/{orderId} (Not actionable)
        var handover = _recipientResolver.ResolveRecipients("RentalHandoverOverdue", orderId, renterId, supplierId);
        Assert.True(handover.IsSuccess);
        var hoSupplier = Assert.Single(handover.Value, r => r.RecipientRole == "Supplier");
        var hoRenter = Assert.Single(handover.Value, r => r.RecipientRole == "Renter");
        Assert.Equal("/own/orders", hoSupplier.DeepLinkUrl);
        Assert.Equal("/own/orders", hoSupplier.ActionUrl);
        Assert.True(hoSupplier.IsActionable);
        Assert.Equal($"/orders/{orderId}", hoRenter.DeepLinkUrl);
        Assert.Null(hoRenter.ActionUrl);
        Assert.False(hoRenter.IsActionable);

        // 5. ReturnConfirmationOverdue: Supplier = /own/orders (Actionable), Renter = /orders/{orderId} (Not actionable)
        var retOverdue = _recipientResolver.ResolveRecipients("ReturnConfirmationOverdue", orderId, renterId, supplierId, returnReqId);
        Assert.True(retOverdue.IsSuccess);
        var roSupplier = Assert.Single(retOverdue.Value, r => r.RecipientRole == "Supplier");
        var roRenter = Assert.Single(retOverdue.Value, r => r.RecipientRole == "Renter");
        Assert.Equal("/own/orders", roSupplier.DeepLinkUrl);
        Assert.Equal("/own/orders", roSupplier.ActionUrl);
        Assert.True(roSupplier.IsActionable);
        Assert.Equal($"/orders/{orderId}", roRenter.DeepLinkUrl);
        Assert.Null(roRenter.ActionUrl);
        Assert.False(roRenter.IsActionable);

        // 6. RentalDeductionDisputeResolved: Supplier = /own/orders (Not actionable), Renter = /orders/{orderId} (Not actionable)
        var dispute = _recipientResolver.ResolveRecipients("RentalDeductionDisputeResolved", orderId, renterId, supplierId);
        Assert.True(dispute.IsSuccess);
        var dispSupplier = Assert.Single(dispute.Value, r => r.RecipientRole == "Supplier");
        var dispRenter = Assert.Single(dispute.Value, r => r.RecipientRole == "Renter");
        Assert.Equal("/own/orders", dispSupplier.DeepLinkUrl);
        Assert.Null(dispSupplier.ActionUrl);
        Assert.False(dispSupplier.IsActionable);
        Assert.Equal($"/orders/{orderId}", dispRenter.DeepLinkUrl);
        Assert.Null(dispRenter.ActionUrl);
        Assert.False(dispRenter.IsActionable);

        // 7. SupplierPayoutCompleted: Supplier = /own/orders (Not actionable)
        var payout = _recipientResolver.ResolveRecipients("SupplierPayoutCompleted", orderId, null, supplierId);
        Assert.True(payout.IsSuccess);
        var payoutSupplier = Assert.Single(payout.Value);
        Assert.Equal("/own/orders", payoutSupplier.DeepLinkUrl);
        Assert.Null(payoutSupplier.ActionUrl);
        Assert.False(payoutSupplier.IsActionable);

        // 8. TemplateRenderer fallback deepLinkUrl tests
        var renderOrder = _renderer.Render("ORDER_PAID", new Dictionary<string, object?> { ["rentalOrderId"] = orderId, ["orderCode"] = "ORD-01", ["paidAmount"] = 1000m, ["invoiceRef"] = "INV-01" });
        Assert.True(renderOrder.IsSuccess);
        Assert.Contains($"/orders/{orderId}", renderOrder.Value.RenderDataJson);

        var renderSettlement = _renderer.Render("PAYOUT_COMPLETED", new Dictionary<string, object?> { ["settlementId"] = Guid.NewGuid(), ["payoutRef"] = "P-01", ["amount"] = 500m });
        Assert.True(renderSettlement.IsSuccess);
        Assert.Contains("/own/orders", renderSettlement.Value.RenderDataJson);

        var renderPayout = _renderer.Render("PAYOUT_COMPLETED", new Dictionary<string, object?> { ["payoutId"] = Guid.NewGuid(), ["payoutRef"] = "P-02", ["amount"] = 500m });
        Assert.True(renderPayout.IsSuccess);
        Assert.Contains("/own/orders", renderPayout.Value.RenderDataJson);
    }
}
