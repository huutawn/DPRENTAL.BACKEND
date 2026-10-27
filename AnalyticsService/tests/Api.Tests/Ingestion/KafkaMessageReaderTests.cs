using System.Globalization;
using Dprental.AnalyticsService.Api.Tests.TestHelpers;
using Dprental.AnalyticsService.Application.Events;
using Dprental.AnalyticsService.Application.Events.Rental;
using Dprental.AnalyticsService.Common;
using Dprental.AnalyticsService.Common.Utils;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests.Ingestion;

public sealed class KafkaMessageReaderTests
{
    [Fact]
    public void UnknownSensitiveFields_AreIgnored()
    {
        string json = DispatcherHarness.Envelope(RentalAnalyticsEventTypes.RentalOrderPaid, new
        {
            rentalOrderId = Guid.NewGuid(), paidAt = DateTimeOffset.UtcNow, orderVersion = 2,
            bankAccount = "0123456789", deliveryAddress = "123 Đường A"
        });

        Result<KafkaEnvelope> envelope = KafkaMessageReader.ReadEnvelope(json);
        Result<RentalOrderPaidPayload> payload = KafkaMessageReader.ReadPayload<RentalOrderPaidPayload>(envelope.Value);

        Assert.True(payload.IsSuccess);
        Assert.Equal(2, payload.Value.OrderVersion);
    }

    [Fact]
    public void MissingOrderVersion_IsNull()
    {
        string json = DispatcherHarness.Envelope(RentalAnalyticsEventTypes.RentalOrderPaid,
            new { rentalOrderId = Guid.NewGuid(), paidAt = DateTimeOffset.UtcNow });

        Result<RentalOrderPaidPayload> payload =
            KafkaMessageReader.ReadPayload<RentalOrderPaidPayload>(KafkaMessageReader.ReadEnvelope(json).Value);

        Assert.Null(payload.Value.OrderVersion); // không còn mặc định thành 1
    }

    [Fact]
    public void NullForNonNullableString_IsSchemaError()
    {
        string json = DispatcherHarness.Envelope(RentalAnalyticsEventTypes.RentalOrderCreated,
            new { rentalOrderId = Guid.NewGuid(), currency = (string?)null, createdAt = DateTimeOffset.UtcNow, items = Array.Empty<object>() });

        Result<RentalOrderCreatedPayload> payload =
            KafkaMessageReader.ReadPayload<RentalOrderCreatedPayload>(KafkaMessageReader.ReadEnvelope(json).Value);

        Assert.Equal(KafkaMessageReader.SchemaInvalidErrorCode, payload.Error.Code);
    }

    [Theory]
    [InlineData("6500000", true)]
    [InlineData("6500000.50", false)]
    [InlineData("-1", false)]
    public void VndAmount_AcceptsOnlyNonNegativeWholeNumbers(string amount, bool valid)
    {
        Assert.Equal(valid, VndAmount.ToLong(decimal.Parse(amount, CultureInfo.InvariantCulture)).IsSuccess);
    }
}
