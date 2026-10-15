using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Common;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Contracts;

public sealed class PaymentRentalContractAndEventsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly DateTimeOffset _fixedTime = new(2026, 10, 8, 8, 0, 0, TimeSpan.FromHours(7));

    // 1. DTO Serialization Roundtrips & Whole VND Invariant
    [Fact]
    public void CreateInvoice_RequestAndResponse_SerializeCorrectly_WithWholeVndAmounts()
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        Guid supplierId = Guid.NewGuid();

        CreateInvoiceRequest request = new(
            RentalOrderId: orderId,
            OrderCode: "ORD-20261008-001",
            RenterId: renterId,
            Currency: "VND",
            PaymentDeadline: _fixedTime.AddMinutes(30),
            Items:
            [
                new(itemId, supplierId, 500_000m, 1_000_000m)
            ],
            OperationKey: "op-create-inv-001");

        Assert.Equal(1_500_000m, request.TotalAmount);

        string json = JsonSerializer.Serialize(request, JsonOptions);
        Assert.Contains("\"currency\":\"VND\"", json);
        Assert.Contains("\"rentalAmount\":500000", json);
        Assert.Contains("\"depositAmount\":1000000", json);
        Assert.DoesNotContain("\"totalAmount\":", json); // Decorated with [JsonIgnore]

        CreateInvoiceRequest? reqDeserialized = JsonSerializer.Deserialize<CreateInvoiceRequest>(json, JsonOptions);
        Assert.NotNull(reqDeserialized);
        Assert.Equal(orderId, reqDeserialized.RentalOrderId);
        Assert.Single(reqDeserialized.Items);

        CreateInvoiceResponse response = new(
            InvoiceRef: "INV-20261008-001",
            TotalAmount: 1_500_000m,
            Currency: "VND",
            PaymentDeadline: _fixedTime.AddMinutes(30),
            IsReplay: false);

        string respJson = JsonSerializer.Serialize(response, JsonOptions);
        Assert.Contains("\"isReplay\":false", respJson);
        Assert.Contains("\"totalAmount\":1500000", respJson);

        CreateInvoiceResponse? respDeserialized = JsonSerializer.Deserialize<CreateInvoiceResponse>(respJson, JsonOptions);
        Assert.NotNull(respDeserialized);
        Assert.Equal("INV-20261008-001", respDeserialized.InvoiceRef);
    }

    [Fact]
    public void CancelInvoice_RequestAndResponse_RoundtripSuccessfully()
    {
        CancelInvoiceRequest request = new(
            RentalOrderId: Guid.NewGuid(),
            InvoiceRef: "INV-20261008-002",
            Reason: "Customer timeout",
            OperationKey: "op-cancel-001");

        string reqJson = JsonSerializer.Serialize(request, JsonOptions);
        CancelInvoiceRequest? reqDes = JsonSerializer.Deserialize<CancelInvoiceRequest>(reqJson, JsonOptions);
        Assert.NotNull(reqDes);
        Assert.Equal("INV-20261008-002", reqDes.InvoiceRef);

        CancelInvoiceResponse response = new("INV-20261008-002", true, _fixedTime, false);
        string respJson = JsonSerializer.Serialize(response, JsonOptions);
        CancelInvoiceResponse? respDes = JsonSerializer.Deserialize<CancelInvoiceResponse>(respJson, JsonOptions);
        Assert.NotNull(respDes);
        Assert.True(respDes.Cancelled);
    }

    [Fact]
    public void RequestRefund_RequestAndResponse_RoundtripSuccessfully()
    {
        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();

        RequestRefundRequest request = new(
            RentalOrderId: orderId,
            InvoiceRef: "INV-001",
            PaymentRef: "PAY-001",
            Items:
            [
                new(itemId, 500_000m, 1_000_000m)
            ],
            Reason: "Supplier rejected rental item",
            OperationKey: "op-refund-001");

        Assert.Equal(1_500_000m, request.TotalRefundAmount);

        string json = JsonSerializer.Serialize(request, JsonOptions);
        Assert.DoesNotContain("\"totalRefundAmount\":", json); // [JsonIgnore]

        RequestRefundRequest? des = JsonSerializer.Deserialize<RequestRefundRequest>(json, JsonOptions);
        Assert.NotNull(des);
        Assert.Equal(1_500_000m, des.TotalRefundAmount);

        RequestRefundResponse response = new(
            RefundRef: "REF-001",
            OperationKey: "op-refund-001",
            Status: "Accepted",
            TotalRefundAmount: 1_500_000m,
            Currency: "VND",
            IsReplay: false);

        string respJson = JsonSerializer.Serialize(response, JsonOptions);
        RequestRefundResponse? respDes = JsonSerializer.Deserialize<RequestRefundResponse>(respJson, JsonOptions);
        Assert.NotNull(respDes);
        Assert.Equal("Accepted", respDes.Status);
    }

    [Fact]
    public void SettleRental_RequestAndResponse_EnforcesDepositMinusDeduction()
    {
        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        Guid supplierId = Guid.NewGuid();

        decimal deposit = 1_000_000m;
        decimal deduction = 250_000m;
        decimal refund = deposit - deduction; // 750,000

        SettleRentalRequest request = new(
            RentalOrderId: orderId,
            RentalItemId: itemId,
            InvoiceRef: "INV-001",
            PaymentRef: "PAY-001",
            SupplierId: supplierId,
            DepositAmount: deposit,
            FinalDeductionAmount: deduction,
            RefundedDepositAmount: refund,
            Currency: "VND",
            DecisionRef: "DISP-RES-001",
            OperationKey: "op-settle-001");

        Assert.Equal(750_000m, request.RefundedDepositAmount);

        string json = JsonSerializer.Serialize(request, JsonOptions);
        SettleRentalRequest? des = JsonSerializer.Deserialize<SettleRentalRequest>(json, JsonOptions);
        Assert.NotNull(des);
        Assert.Equal(250_000m, des.FinalDeductionAmount);

        SettleRentalResponse response = new(
            SettlementRef: "SETTLE-001",
            OperationKey: "op-settle-001",
            Status: "Accepted",
            FinalDeductionAmount: deduction,
            RefundedDepositAmount: refund,
            Currency: "VND",
            IsReplay: false);

        string respJson = JsonSerializer.Serialize(response, JsonOptions);
        SettleRentalResponse? respDes = JsonSerializer.Deserialize<SettleRentalResponse>(respJson, JsonOptions);
        Assert.NotNull(respDes);
        Assert.Equal("Accepted", respDes.Status);
    }

    // 2. Integration Event Schemas & Envelopes
    [Fact]
    public void InvoicePaidEvent_SerializesInStandardEnvelope()
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();

        InvoicePaidPayload payload = new(
            RentalOrderId: orderId,
            RenterId: renterId,
            InvoiceRef: "INV-001",
            PaymentRef: "PAY-001",
            PaidAmount: 1_500_000m,
            Currency: "VND",
            PaidAt: _fixedTime);

        Guid messageId = Guid.NewGuid();
        KafkaMessageEnvelope<InvoicePaidPayload> envelope = new(
            messageId,
            PaymentContractSpec.EventTypes.InvoicePaid,
            1,
            "payment-service",
            orderId.ToString("D"),
            _fixedTime,
            "corr-001",
            null,
            payload);

        string json = JsonSerializer.Serialize(envelope, JsonOptions);
        Assert.Contains("\"messageType\":\"InvoicePaid\"", json);
        Assert.Contains("\"paidAmount\":1500000", json);
        Assert.Contains($"\"aggregateId\":\"{orderId}\"", json);

        KafkaMessageEnvelope<InvoicePaidPayload>? des =
            JsonSerializer.Deserialize<KafkaMessageEnvelope<InvoicePaidPayload>>(json, JsonOptions);
        Assert.NotNull(des);
        Assert.Equal("InvoicePaid", des.MessageType);
        Assert.Equal(1_500_000m, des.Payload.PaidAmount);
    }

    [Fact]
    public void RefundCompletedEvent_SerializesItemBreakdown()
    {
        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();

        RefundCompletedPayload payload = new(
            RentalOrderId: orderId,
            InvoiceRef: "INV-001",
            PaymentRef: "PAY-001",
            RefundRef: "REF-001",
            OperationKey: "op-refund-001",
            TotalRefundedAmount: 1_500_000m,
            Currency: "VND",
            Items:
            [
                new(itemId, 500_000m, 1_000_000m)
            ],
            CompletedAt: _fixedTime);

        Guid messageId = Guid.NewGuid();
        Guid causationId = Guid.NewGuid();
        KafkaMessageEnvelope<RefundCompletedPayload> envelope = new(
            messageId,
            PaymentContractSpec.EventTypes.RefundCompleted,
            1,
            "payment-service",
            orderId.ToString("D"),
            _fixedTime,
            "corr-002",
            causationId,
            payload);

        string json = JsonSerializer.Serialize(envelope, JsonOptions);
        Assert.Contains("\"refundedRentalAmount\":500000", json);
        Assert.Contains("\"totalRefundedAmount\":1500000", json);

        KafkaMessageEnvelope<RefundCompletedPayload>? des =
            JsonSerializer.Deserialize<KafkaMessageEnvelope<RefundCompletedPayload>>(json, JsonOptions);
        Assert.NotNull(des);
        Assert.Single(des.Payload.Items);
        Assert.Equal(1_500_000m, des.Payload.Items[0].TotalRefundedAmount);
    }

    [Fact]
    public void RentalSettledEvent_SerializesZeroRefundAndFullDeduction()
    {
        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();

        RentalSettledPayload payload = new(
            RentalOrderId: orderId,
            RentalItemId: itemId,
            InvoiceRef: "INV-001",
            PaymentRef: "PAY-001",
            SettlementRef: "SETTLE-001",
            OperationKey: "op-settle-001",
            FinalDeductionAmount: 1_000_000m,
            RefundedDepositAmount: 0m, // Full deduction, 0 refund
            Currency: "VND",
            RefundRef: null,
            CompletedAt: _fixedTime);

        Guid messageId = Guid.NewGuid();
        KafkaMessageEnvelope<RentalSettledPayload> envelope = new(
            messageId,
            PaymentContractSpec.EventTypes.RentalSettled,
            1,
            "payment-service",
            orderId.ToString("D"),
            _fixedTime,
            "corr-003",
            null,
            payload);

        string json = JsonSerializer.Serialize(envelope, JsonOptions);
        Assert.Contains("\"refundedDepositAmount\":0", json);
        Assert.Contains("\"finalDeductionAmount\":1000000", json);

        KafkaMessageEnvelope<RentalSettledPayload>? des =
            JsonSerializer.Deserialize<KafkaMessageEnvelope<RentalSettledPayload>>(json, JsonOptions);
        Assert.NotNull(des);
        Assert.Equal(0m, des.Payload.RefundedDepositAmount);
        Assert.Equal(1_000_000m, des.Payload.FinalDeductionAmount);
    }

    // 3. Payment Request DTO Boundary Validation Tests
    [Fact]
    public void CreateInvoiceRequest_Validate_RejectsNonVndCurrency()
    {
        CreateInvoiceRequest req = new(
            RentalOrderId: Guid.NewGuid(),
            OrderCode: "ORD-001",
            RenterId: Guid.NewGuid(),
            Currency: "USD",
            PaymentDeadline: _fixedTime.AddMinutes(30),
            Items: [new(Guid.NewGuid(), Guid.NewGuid(), 100_000m, 200_000m)],
            OperationKey: "op-key");

        Result result = req.Validate();
        Assert.True(result.IsFailure);
        Assert.Equal("Payment.InvalidCurrency", result.Error.Code);
    }

    [Fact]
    public void CreateInvoiceRequest_Validate_RejectsEmptyOrDuplicateItems()
    {
        Guid itemId = Guid.NewGuid();
        CreateInvoiceRequest dupReq = new(
            RentalOrderId: Guid.NewGuid(),
            OrderCode: "ORD-001",
            RenterId: Guid.NewGuid(),
            Currency: "VND",
            PaymentDeadline: _fixedTime.AddMinutes(30),
            Items:
            [
                new(itemId, Guid.NewGuid(), 100_000m, 200_000m),
                new(itemId, Guid.NewGuid(), 100_000m, 200_000m)
            ],
            OperationKey: "op-key");

        Result dupResult = dupReq.Validate();
        Assert.True(dupResult.IsFailure);
        Assert.Equal("Payment.DuplicateItemId", dupResult.Error.Code);

        CreateInvoiceRequest emptyReq = new(
            RentalOrderId: Guid.NewGuid(),
            OrderCode: "ORD-001",
            RenterId: Guid.NewGuid(),
            Currency: "VND",
            PaymentDeadline: _fixedTime.AddMinutes(30),
            Items: [],
            OperationKey: "op-key");

        Result emptyResult = emptyReq.Validate();
        Assert.True(emptyResult.IsFailure);
        Assert.Equal("Payment.EmptyItems", emptyResult.Error.Code);
    }

    [Fact]
    public void RequestRefundRequest_Validate_RejectsZeroOrNegativeTotalRefund()
    {
        RequestRefundRequest req = new(
            RentalOrderId: Guid.NewGuid(),
            InvoiceRef: "INV-001",
            PaymentRef: "PAY-001",
            Items: [new(Guid.NewGuid(), 0m, 0m)],
            Reason: "Refund reason",
            OperationKey: "op-ref");

        Result result = req.Validate();
        Assert.True(result.IsFailure);
        Assert.Equal("Payment.InvalidRefundAmount", result.Error.Code);
    }

    [Fact]
    public void SettleRentalRequest_Validate_EnforcesBreakdownFormulaAndDepositCeiling()
    {
        // 1. Deduction exceeds deposit
        SettleRentalRequest exceedReq = new(
            RentalOrderId: Guid.NewGuid(),
            RentalItemId: Guid.NewGuid(),
            InvoiceRef: "INV-001",
            PaymentRef: "PAY-001",
            SupplierId: Guid.NewGuid(),
            DepositAmount: 500_000m,
            FinalDeductionAmount: 600_000m,
            RefundedDepositAmount: -100_000m,
            Currency: "VND",
            DecisionRef: "DEC-001",
            OperationKey: "op-settle");

        Result exceedRes = exceedReq.Validate();
        Assert.True(exceedRes.IsFailure);

        // 2. Breakdown mismatch: refunded != deposit - deduction
        SettleRentalRequest mismatchReq = new(
            RentalOrderId: Guid.NewGuid(),
            RentalItemId: Guid.NewGuid(),
            InvoiceRef: "INV-001",
            PaymentRef: "PAY-001",
            SupplierId: Guid.NewGuid(),
            DepositAmount: 500_000m,
            FinalDeductionAmount: 200_000m,
            RefundedDepositAmount: 250_000m, // Expected 300,000!
            Currency: "VND",
            DecisionRef: "DEC-001",
            OperationKey: "op-settle");

        Result mismatchRes = mismatchReq.Validate();
        Assert.True(mismatchRes.IsFailure);
        Assert.Equal("Payment.SettlementBreakdownMismatch", mismatchRes.Error.Code);

        // 3. Valid settlement request
        SettleRentalRequest validReq = new(
            RentalOrderId: Guid.NewGuid(),
            RentalItemId: Guid.NewGuid(),
            InvoiceRef: "INV-001",
            PaymentRef: "PAY-001",
            SupplierId: Guid.NewGuid(),
            DepositAmount: 500_000m,
            FinalDeductionAmount: 200_000m,
            RefundedDepositAmount: 300_000m,
            Currency: "VND",
            DecisionRef: "DEC-001",
            OperationKey: "op-settle");

        Result validRes = validReq.Validate();
        Assert.True(validRes.IsSuccess);
    }
}
