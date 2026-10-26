using Dprental.AnalyticsService.Application.Kafka.Contracts;
using Dprental.AnalyticsService.Application.Kafka.Contracts.Payment;
using Dprental.AnalyticsService.Application.Kafka.Contracts.Rental;
using Dprental.AnalyticsService.Common;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests.Kafka;

public sealed class KafkaMessageReaderTests
{
    // Theo mẫu RentalOrderCreated trong README Kafka của Rental; kèm 2 field lạ (bankAccount, deliveryAddress)
    // để chứng minh dữ liệu nhạy cảm bị bỏ qua khi đọc.
    private const string RentalOrderCreatedJson = """
        {
          "messageId": "550e8400-e29b-41d4-a716-446655440000",
          "messageType": "RentalOrderCreated",
          "version": 1,
          "producer": "RentalService",
          "aggregateId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
          "occurredAt": "2026-10-23T17:00:00+07:00",
          "correlationId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
          "payload": {
            "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
            "orderCode": "RNT-20261023-0001",
            "renterId": "b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e",
            "currency": "VND",
            "totalRentalAmount": 900000.00,
            "totalDepositAmount": 3000000.00,
            "totalPayableAmount": 3900000.00,
            "paymentDeadline": "2026-10-23T10:30:00Z",
            "createdAt": "2026-10-23T10:00:00Z",
            "bankAccount": "0123456789",
            "items": [
              {
                "rentalItemId": "c3d4e5f6-a7b8-9c0d-1e2f-3a4b5c6d7e8f",
                "equipmentListingId": "d4e5f6a7-b8c9-0d1e-2f3a-4b5c6d7e8f90",
                "supplierId": "e5f6a7b8-c9d0-1e2f-3a4b-5c6d7e8f90a1",
                "quantity": 3,
                "expectedStartDate": "2026-10-25",
                "expectedEndDate": "2026-10-28",
                "rentalAmount": 900000.00,
                "depositAmount": 3000000.00,
                "equipmentTypeId": "f6a7b8c9-d0e1-2f3a-4b5c-6d7e8f90a1b2",
                "equipmentTypeName": "Máy đầm cóc",
                "brandId": null,
                "brandName": null,
                "model": null,
                "deliveryAddress": "123 Đường A"
              }
            ],
            "orderVersion": 1
          }
        }
        """;

    [Fact]
    public void RentalOrderCreated_ReadsEnvelopeAndPayload()
    {
        KafkaEnvelope envelope = ReadEnvelopeOrFail(RentalOrderCreatedJson);
        Result<RentalOrderCreatedPayload> result = KafkaMessageReader.ReadPayload<RentalOrderCreatedPayload>(envelope);

        Assert.True(result.IsSuccess);
        RentalOrderCreatedPayload payload = result.Value;
        Assert.Equal(AnalyticsMessageTypes.RentalOrderCreated, envelope.MessageType);
        Assert.Equal("RNT-20261023-0001", payload.OrderCode);
        Assert.Equal(900000m, payload.TotalRentalAmount);
        Assert.Equal(1, payload.OrderVersion);

        RentalOrderCreatedItemPayload item = Assert.Single(payload.Items);
        Assert.Equal(3, item.Quantity);
        Assert.Equal(new DateOnly(2026, 10, 25), item.ExpectedStartDate);
        Assert.Equal("Máy đầm cóc", item.EquipmentTypeName);
        Assert.Null(item.BrandId); // snapshot thiếu → null, không bịa giá trị
    }

    [Fact]
    public void OccurredAtWithVietnamOffset_KeepsSameInstantAsUtc()
    {
        KafkaEnvelope envelope = ReadEnvelopeOrFail(RentalOrderCreatedJson);

        // 17:00 giờ VN (+07:00) là cùng một thời điểm với 10:00 UTC
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero), envelope.OccurredAt);
    }

    [Fact]
    public void MissingOrderVersion_IsNullNotZero()
    {
        KafkaEnvelope envelope = ReadEnvelopeOrFail(Envelope("RentalOrderPaid", """
            { "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d", "paidAt": "2026-10-23T10:15:00Z" }
            """));

        Result<RentalOrderPaidPayload> result = KafkaMessageReader.ReadPayload<RentalOrderPaidPayload>(envelope);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.OrderVersion);
    }

    [Fact]
    public void MissingRequiredField_FailsWithSchemaError()
    {
        KafkaEnvelope envelope = ReadEnvelopeOrFail(Envelope("RentalOrderPaid", """
            { "paidAt": "2026-10-23T10:15:00Z", "orderVersion": 2 }
            """));

        Result<RentalOrderPaidPayload> result = KafkaMessageReader.ReadPayload<RentalOrderPaidPayload>(envelope);

        AssertSchemaInvalid(result);
    }

    [Fact]
    public void NullForNonNullableString_FailsWithSchemaError()
    {
        KafkaEnvelope envelope = ReadEnvelopeOrFail(Envelope("RentalOrderCancelled", """
            { "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d", "cancelledAt": "2026-10-23T11:00:00Z", "reason": null }
            """));

        Result<RentalOrderCancelledPayload> result = KafkaMessageReader.ReadPayload<RentalOrderCancelledPayload>(envelope);

        AssertSchemaInvalid(result);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{ "messageType": "RentalOrderPaid" }""")] // thiếu messageId, occurredAt, payload
    public void InvalidEnvelope_FailsWithSchemaError(string json)
    {
        AssertSchemaInvalid(KafkaMessageReader.ReadEnvelope(json));
    }

    [Fact]
    public void RefundCompleted_ReadsItemsWithSeparateRentalAndDeposit()
    {
        KafkaEnvelope envelope = ReadEnvelopeOrFail(Envelope("RefundCompleted", """
            {
              "rentalOrderId": "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
              "invoiceRef": "INV-1", "paymentRef": "PAY-1",
              "refundRef": "RF-001", "operationKey": "op-1",
              "totalRefundedAmount": 26000000, "currency": "VND",
              "items": [
                { "rentalItemId": "c3d4e5f6-a7b8-9c0d-1e2f-3a4b5c6d7e8f", "refundedRentalAmount": 6000000, "refundedDepositAmount": 20000000 }
              ],
              "completedAt": "2026-11-11T08:00:00Z"
            }
            """));

        Result<RefundCompletedPayload> result = KafkaMessageReader.ReadPayload<RefundCompletedPayload>(envelope);

        Assert.True(result.IsSuccess);
        RefundCompletedItemPayload item = Assert.Single(result.Value.Items);
        Assert.Equal(6000000m, item.RefundedRentalAmount);
        Assert.Equal(20000000m, item.RefundedDepositAmount);
    }

    private static string Envelope(string messageType, string payloadJson) => $$"""
        {
          "messageId": "660e8400-e29b-41d4-a716-446655440001",
          "messageType": "{{messageType}}",
          "version": 1,
          "producer": "test",
          "occurredAt": "2026-10-23T10:15:00Z",
          "payload": {{payloadJson}}
        }
        """;

    private static KafkaEnvelope ReadEnvelopeOrFail(string json)
    {
        Result<KafkaEnvelope> result = KafkaMessageReader.ReadEnvelope(json);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        return result.Value;
    }

    private static void AssertSchemaInvalid<T>(Result<T> result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(KafkaMessageReader.SchemaInvalidErrorCode, result.Error.Code);
    }
}
