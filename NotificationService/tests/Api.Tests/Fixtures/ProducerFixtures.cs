namespace Dprental.NotificationService.Api.Tests.Fixtures;

public static class ProducerFixtures
{
    public static string RentalOrderCreated(
        Guid messageId,
        Guid orderId,
        Guid renterId,
        string orderCode = "RNT-20261102-0001",
        decimal totalPayable = 1500000m,
        long orderVersion = 1) => $$"""
    {
      "messageId": "{{messageId}}",
      "messageType": "RentalOrderCreated",
      "version": 1,
      "producer": "RentalService",
      "aggregateId": "{{orderId}}",
      "occurredAt": "2026-11-04T08:00:00.0000000+00:00",
      "correlationId": "corr-{{orderId}}",
      "causationId": null,
      "payload": {
        "rentalOrderId": "{{orderId}}",
        "orderCode": "{{orderCode}}",
        "renterId": "{{renterId}}",
        "currency": "VND",
        "totalRentalAmount": 1000000,
        "totalDepositAmount": 500000,
        "totalPayableAmount": {{totalPayable}},
        "paymentDeadline": "2026-11-04T08:30:00.0000000+00:00",
        "createdAt": "2026-11-04T08:00:00.0000000+00:00",
        "items": [
          {
            "rentalItemId": "{{Guid.NewGuid()}}",
            "equipmentListingId": "{{Guid.NewGuid()}}",
            "supplierId": "{{Guid.NewGuid()}}",
            "quantity": 1,
            "expectedStartDate": "2026-11-10",
            "expectedEndDate": "2026-11-12",
            "rentalAmount": 1000000,
            "depositAmount": 500000
          }
        ],
        "orderVersion": {{orderVersion}}
      }
    }
    """;

    public static string RentalOrderPaid(
        Guid messageId,
        Guid orderId,
        Guid renterId,
        decimal paidAmount = 1500000m,
        string invoiceRef = "INV-20261104-001",
        string paymentRef = "PAY-20261104-001",
        long orderVersion = 2) => $$"""
    {
      "messageId": "{{messageId}}",
      "messageType": "RentalOrderPaid",
      "version": 1,
      "producer": "RentalService",
      "aggregateId": "{{orderId}}",
      "occurredAt": "2026-11-04T08:15:00.0000000+00:00",
      "correlationId": "corr-{{orderId}}",
      "causationId": null,
      "payload": {
        "rentalOrderId": "{{orderId}}",
        "renterId": "{{renterId}}",
        "invoiceRef": "{{invoiceRef}}",
        "paymentRef": "{{paymentRef}}",
        "paidAmount": {{paidAmount}},
        "paidAt": "2026-11-04T08:15:00.0000000+00:00",
        "orderVersion": {{orderVersion}}
      }
    }
    """;

    public static string RentalSupplierConfirmationRequested(
        Guid messageId,
        Guid orderId,
        Guid itemId,
        Guid supplierId,
        long orderVersion = 2) => $$"""
    {
      "messageId": "{{messageId}}",
      "messageType": "RentalSupplierConfirmationRequested",
      "version": 1,
      "producer": "RentalService",
      "aggregateId": "{{orderId}}",
      "occurredAt": "2026-11-04T08:16:00.0000000+00:00",
      "correlationId": "corr-{{orderId}}",
      "causationId": null,
      "payload": {
        "rentalOrderId": "{{orderId}}",
        "rentalItemId": "{{itemId}}",
        "supplierId": "{{supplierId}}",
        "equipmentListingId": "{{Guid.NewGuid()}}",
        "quantity": 1,
        "supplierConfirmationDeadline": "2026-11-05T08:16:00.0000000+00:00",
        "orderVersion": {{orderVersion}}
      }
    }
    """;

    public static string RentalOrderExpired(
        Guid messageId,
        Guid orderId,
        Guid renterId,
        string reason = "Hết thời gian thanh toán",
        long orderVersion = 3) => $$"""
    {
      "messageId": "{{messageId}}",
      "messageType": "RentalOrderExpired",
      "version": 1,
      "producer": "RentalService",
      "aggregateId": "{{orderId}}",
      "occurredAt": "2026-11-04T08:35:00.0000000+00:00",
      "correlationId": "corr-{{orderId}}",
      "causationId": null,
      "payload": {
        "rentalOrderId": "{{orderId}}",
        "renterId": "{{renterId}}",
        "expiredAt": "2026-11-04T08:35:00.0000000+00:00",
        "reason": "{{reason}}",
        "orderVersion": {{orderVersion}}
      }
    }
    """;

    public static string RentalOrderCancelled(
        Guid messageId,
        Guid orderId,
        Guid renterId,
        string reason = "Khách hàng hủy đơn trước thanh toán",
        long orderVersion = 3) => $$"""
    {
      "messageId": "{{messageId}}",
      "messageType": "RentalOrderCancelled",
      "version": 1,
      "producer": "RentalService",
      "aggregateId": "{{orderId}}",
      "occurredAt": "2026-11-04T08:20:00.0000000+00:00",
      "correlationId": "corr-{{orderId}}",
      "causationId": null,
      "payload": {
        "rentalOrderId": "{{orderId}}",
        "renterId": "{{renterId}}",
        "cancelledAt": "2026-11-04T08:20:00.0000000+00:00",
        "reason": "{{reason}}",
        "orderVersion": {{orderVersion}}
      }
    }
    """;

    public static string RentalItemHandoverCompleted(
        Guid messageId,
        Guid orderId,
        Guid itemId,
        Guid renterId,
        Guid supplierId,
        long orderVersion = 3) => $$"""
    {
      "messageId": "{{messageId}}",
      "messageType": "RentalItemHandoverCompleted",
      "version": 1,
      "producer": "RentalService",
      "aggregateId": "{{orderId}}",
      "occurredAt": "2026-11-04T09:00:00.0000000+00:00",
      "correlationId": "corr-{{orderId}}",
      "causationId": null,
      "payload": {
        "rentalOrderId": "{{orderId}}",
        "rentalItemId": "{{itemId}}",
        "renterId": "{{renterId}}",
        "supplierId": "{{supplierId}}",
        "equipmentListingId": "{{Guid.NewGuid()}}",
        "quantity": 1,
        "handoverRecordId": "{{Guid.NewGuid()}}",
        "actualHandoverAt": "2026-11-04T09:00:00.0000000+00:00",
        "completedAt": "2026-11-04T09:05:00.0000000+00:00",
        "orderVersion": {{orderVersion}}
      }
    }
    """;

    public static string RentalHandoverOverdue(
        Guid messageId,
        Guid orderId,
        Guid itemId,
        Guid renterId,
        Guid supplierId,
        long orderVersion = 3) => $$"""
    {
      "messageId": "{{messageId}}",
      "messageType": "RentalHandoverOverdue",
      "version": 1,
      "producer": "RentalService",
      "aggregateId": "{{orderId}}",
      "occurredAt": "2026-11-04T10:00:00.0000000+00:00",
      "correlationId": "corr-{{orderId}}",
      "causationId": null,
      "payload": {
        "rentalOrderId": "{{orderId}}",
        "rentalItemId": "{{itemId}}",
        "supplierId": "{{supplierId}}",
        "renterId": "{{renterId}}",
        "expectedStartDate": "2026-11-04",
        "overdueAt": "2026-11-04T10:00:00.0000000+00:00",
        "orderVersion": {{orderVersion}}
      }
    }
    """;

    public static string ReturnConfirmationOverdue(
        Guid messageId,
        Guid orderId,
        Guid itemId,
        Guid returnRequestId,
        Guid renterId,
        Guid supplierId,
        long orderVersion = 4) => $$"""
    {
      "messageId": "{{messageId}}",
      "messageType": "ReturnConfirmationOverdue",
      "version": 1,
      "producer": "RentalService",
      "aggregateId": "{{orderId}}",
      "occurredAt": "2026-11-04T11:00:00.0000000+00:00",
      "correlationId": "corr-{{orderId}}",
      "causationId": null,
      "payload": {
        "rentalOrderId": "{{orderId}}",
        "rentalItemId": "{{itemId}}",
        "returnRequestId": "{{returnRequestId}}",
        "supplierId": "{{supplierId}}",
        "renterId": "{{renterId}}",
        "confirmationDeadlineAt": "2026-11-04T10:00:00.0000000+00:00",
        "overdueAt": "2026-11-04T11:00:00.0000000+00:00",
        "overdueAssignmentIds": ["{{Guid.NewGuid()}}"],
        "orderVersion": {{orderVersion}}
      }
    }
    """;

    public static string RentalOrderCompleted(
        Guid messageId,
        Guid orderId,
        Guid renterId,
        long orderVersion = 5) => $$"""
    {
      "messageId": "{{messageId}}",
      "messageType": "RentalOrderCompleted",
      "version": 1,
      "producer": "RentalService",
      "aggregateId": "{{orderId}}",
      "occurredAt": "2026-11-04T12:00:00.0000000+00:00",
      "correlationId": "corr-{{orderId}}",
      "causationId": null,
      "payload": {
        "rentalOrderId": "{{orderId}}",
        "renterId": "{{renterId}}",
        "completedAt": "2026-11-04T12:00:00.0000000+00:00",
        "orderVersion": {{orderVersion}}
      }
    }
    """;

    public static string LatePaymentRefundRequested(
        Guid messageId,
        Guid orderId,
        Guid renterId,
        decimal totalRefundAmount = 1500000m,
        string invoiceRef = "INV-20261104-001",
        string paymentRef = "PAY-20261104-001",
        long orderVersion = 2) => $$"""
    {
      "messageId": "{{messageId}}",
      "messageType": "LatePaymentRefundRequested",
      "version": 1,
      "producer": "RentalService",
      "aggregateId": "{{orderId}}",
      "occurredAt": "2026-11-04T08:35:00.0000000+00:00",
      "correlationId": "corr-{{orderId}}",
      "causationId": null,
      "payload": {
        "rentalOrderId": "{{orderId}}",
        "renterId": "{{renterId}}",
        "invoiceRef": "{{invoiceRef}}",
        "paymentRef": "{{paymentRef}}",
        "totalRefundAmount": {{totalRefundAmount}},
        "requestedAt": "2026-11-04T08:35:00.0000000+00:00",
        "orderVersion": {{orderVersion}}
      }
    }
    """;

    public static string RefundCompletedFinancial(
        Guid messageId,
        Guid refundId,
        Guid orderId,
        Guid renterId,
        decimal amount = 1500000m) => $$"""
    {
      "messageId": "{{messageId}}",
      "messageType": "RefundCompletedFinancial",
      "version": 1,
      "producer": "payment-service",
      "aggregateId": "{{orderId}}",
      "occurredAt": "2026-11-04T09:00:00.0000000+00:00",
      "correlationId": "corr-{{orderId}}",
      "causationId": null,
      "payload": {
        "refundId": "{{refundId}}",
        "refundRef": "REF-{{refundId.ToString()[..8]}}",
        "rentalOrderId": "{{orderId}}",
        "rentalItemId": null,
        "renterId": "{{renterId}}",
        "refundKind": "FULL_CANCELLATION",
        "amount": {{amount}},
        "currency": "VND",
        "completedAt": "2026-11-04T09:00:00.0000000+00:00"
      }
    }
    """;

    public static string SupplierPayoutCompleted(
        Guid messageId,
        Guid payoutId,
        Guid settlementId,
        Guid itemId,
        Guid supplierId,
        decimal amount = 900000m) => $$"""
    {
      "messageId": "{{messageId}}",
      "messageType": "SupplierPayoutCompleted",
      "version": 1,
      "producer": "payment-service",
      "aggregateId": "{{settlementId}}",
      "occurredAt": "2026-11-04T12:00:00.0000000+00:00",
      "correlationId": "corr-{{settlementId}}",
      "causationId": null,
      "payload": {
        "payoutId": "{{payoutId}}",
        "payoutRef": "PAYOUT-{{payoutId.ToString()[..8]}}",
        "settlementId": "{{settlementId}}",
        "rentalItemId": "{{itemId}}",
        "supplierId": "{{supplierId}}",
        "amount": {{amount}},
        "currency": "VND",
        "completedAt": "2026-11-04T12:00:00.0000000+00:00"
      }
    }
    """;

    public static string RentalItemConfirmed(
        Guid messageId,
        Guid orderId,
        Guid itemId,
        Guid supplierId,
        Guid renterId,
        string inventoryConfirmationRef = "INV-CONF-001",
        string orderCode = "RNT-20261102-0001",
        long orderVersion = 3) => $$"""
    {
      "messageId": "{{messageId}}",
      "messageType": "RentalItemConfirmed",
      "version": 1,
      "producer": "RentalService",
      "aggregateId": "{{orderId}}",
      "occurredAt": "2026-11-04T08:30:00.0000000+00:00",
      "correlationId": "corr-{{orderId}}",
      "causationId": null,
      "payload": {
        "rentalOrderId": "{{orderId}}",
        "rentalItemId": "{{itemId}}",
        "supplierId": "{{supplierId}}",
        "renterId": "{{renterId}}",
        "orderCode": "{{orderCode}}",
        "decisionAt": "2026-11-04T08:30:00.0000000+00:00",
        "inventoryConfirmationRef": "{{inventoryConfirmationRef}}",
        "orderVersion": {{orderVersion}}
      }
    }
    """;

    public static string RentalItemRejected(
        Guid messageId,
        Guid orderId,
        Guid itemId,
        Guid supplierId,
        Guid renterId,
        string reason = "Thiết bị đang bảo trì",
        string? optionalNote = "Không thể đáp ứng lịch thuê",
        string orderCode = "RNT-20261102-0001",
        long orderVersion = 3) => $$"""
    {
      "messageId": "{{messageId}}",
      "messageType": "RentalItemRejected",
      "version": 1,
      "producer": "RentalService",
      "aggregateId": "{{orderId}}",
      "occurredAt": "2026-11-04T08:30:00.0000000+00:00",
      "correlationId": "corr-{{orderId}}",
      "causationId": null,
      "payload": {
        "rentalOrderId": "{{orderId}}",
        "rentalItemId": "{{itemId}}",
        "supplierId": "{{supplierId}}",
        "renterId": "{{renterId}}",
        "orderCode": "{{orderCode}}",
        "decisionAt": "2026-11-04T08:30:00.0000000+00:00",
        "reason": "{{reason}}",
        "optionalNote": {{(optionalNote != null ? $"\"{optionalNote}\"" : "null")}},
        "orderVersion": {{orderVersion}}
      }
    }
    """;

    public static string RentalOrderRejected(
        Guid messageId,
        Guid orderId,
        Guid renterId,
        Guid? supplierId = null,
        string reason = "Đơn thuê bị từ chối",
        string orderCode = "RNT-20261102-0001",
        long orderVersion = 3) => $$"""
    {
      "messageId": "{{messageId}}",
      "messageType": "RentalOrderRejected",
      "version": 1,
      "producer": "RentalService",
      "aggregateId": "{{orderId}}",
      "occurredAt": "2026-11-04T08:30:00.0000000+00:00",
      "correlationId": "corr-{{orderId}}",
      "causationId": null,
      "payload": {
        "rentalOrderId": "{{orderId}}",
        "renterId": "{{renterId}}",
        "supplierId": {{(supplierId.HasValue ? $"\"{supplierId.Value}\"" : "null")}},
        "orderCode": "{{orderCode}}",
        "decisionAt": "2026-11-04T08:30:00.0000000+00:00",
        "reason": "{{reason}}",
        "orderVersion": {{orderVersion}}
      }
    }
    """;

    public static string RentalItemExpired(
        Guid messageId,
        Guid orderId,
        Guid itemId,
        Guid renterId,
        string reason = "Hết hạn giữ chỗ",
        string orderCode = "RNT-20261102-0001",
        long orderVersion = 3) => $$"""
    {
      "messageId": "{{messageId}}",
      "messageType": "RentalItemExpired",
      "version": 1,
      "producer": "RentalService",
      "aggregateId": "{{orderId}}",
      "occurredAt": "2026-11-04T08:30:00.0000000+00:00",
      "correlationId": "corr-{{orderId}}",
      "causationId": null,
      "payload": {
        "rentalOrderId": "{{orderId}}",
        "rentalItemId": "{{itemId}}",
        "renterId": "{{renterId}}",
        "orderCode": "{{orderCode}}",
        "reason": "{{reason}}",
        "expiredAt": "2026-11-04T08:30:00.0000000+00:00",
        "orderVersion": {{orderVersion}}
      }
    }
    """;

    public static string RentalItemCancelled(
        Guid messageId,
        Guid orderId,
        Guid itemId,
        Guid renterId,
        string reason = "Hủy món đồ",
        string orderCode = "RNT-20261102-0001",
        long orderVersion = 3) => $$"""
    {
      "messageId": "{{messageId}}",
      "messageType": "RentalItemCancelled",
      "version": 1,
      "producer": "RentalService",
      "aggregateId": "{{orderId}}",
      "occurredAt": "2026-11-04T08:30:00.0000000+00:00",
      "correlationId": "corr-{{orderId}}",
      "causationId": null,
      "payload": {
        "rentalOrderId": "{{orderId}}",
        "rentalItemId": "{{itemId}}",
        "renterId": "{{renterId}}",
        "orderCode": "{{orderCode}}",
        "reason": "{{reason}}",
        "cancelledAt": "2026-11-04T08:30:00.0000000+00:00",
        "orderVersion": {{orderVersion}}
      }
    }
    """;

    public static string RentalItemReturned(
        Guid messageId,
        Guid orderId,
        Guid itemId,
        Guid renterId,
        Guid supplierId,
        Guid? returnRequestId = null,
        string orderCode = "RNT-20261102-0001",
        long orderVersion = 4) => $$"""
    {
      "messageId": "{{messageId}}",
      "messageType": "RentalItemReturned",
      "version": 1,
      "producer": "RentalService",
      "aggregateId": "{{orderId}}",
      "occurredAt": "2026-11-04T10:00:00.0000000+00:00",
      "correlationId": "corr-{{orderId}}",
      "causationId": null,
      "payload": {
        "rentalOrderId": "{{orderId}}",
        "rentalItemId": "{{itemId}}",
        "returnRequestId": {{(returnRequestId.HasValue ? $"\"{returnRequestId.Value}\"" : "null")}},
        "renterId": "{{renterId}}",
        "supplierId": "{{supplierId}}",
        "orderCode": "{{orderCode}}",
        "returnedAt": "2026-11-04T10:00:00.0000000+00:00",
        "orderVersion": {{orderVersion}}
      }
    }
    """;

    public static string RentalItemCompleted(
        Guid messageId,
        Guid orderId,
        Guid itemId,
        Guid renterId,
        Guid supplierId,
        string orderCode = "RNT-20261102-0001",
        long orderVersion = 5) => $$"""
    {
      "messageId": "{{messageId}}",
      "messageType": "RentalItemCompleted",
      "version": 1,
      "producer": "RentalService",
      "aggregateId": "{{orderId}}",
      "occurredAt": "2026-11-04T11:00:00.0000000+00:00",
      "correlationId": "corr-{{orderId}}",
      "causationId": null,
      "payload": {
        "rentalOrderId": "{{orderId}}",
        "rentalItemId": "{{itemId}}",
        "renterId": "{{renterId}}",
        "supplierId": "{{supplierId}}",
        "orderCode": "{{orderCode}}",
        "completedAt": "2026-11-04T11:00:00.0000000+00:00",
        "orderVersion": {{orderVersion}}
      }
    }
    """;
}
