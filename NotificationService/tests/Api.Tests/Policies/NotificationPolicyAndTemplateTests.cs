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
}
