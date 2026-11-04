using Dprental.NotificationService.Application.Policies;
using Dprental.NotificationService.Application.Recipients;
using Dprental.NotificationService.Application.Templates;
using Dprental.NotificationService.Domain.Entities;
using Xunit;

namespace Dprental.NotificationService.Api.Tests.Policies;

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
        Assert.Equal($"/renter/orders/{orderId}", confirmedRecipient.DeepLinkUrl);

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
        Assert.Equal($"/renter/orders/{orderId}", rejectedRecipient.DeepLinkUrl);

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
        Assert.Equal($"/renter/orders/{orderId}", refundRecipient.DeepLinkUrl);

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
        Assert.Equal($"/supplier/settlements/{settlementId}", payoutRecipient.DeepLinkUrl);
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
        Assert.Contains($"/renter/orders/{orderId}", result.Value.EmailBody);
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
        Assert.Contains($"/renter/orders/{orderId}", result.Value.EmailBody);
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
        Assert.Contains($"/renter/orders/{orderId}", result.Value.EmailBody);
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
        Assert.Contains($"/supplier/settlements/{settlementId}", result.Value.EmailBody);
    }
}
