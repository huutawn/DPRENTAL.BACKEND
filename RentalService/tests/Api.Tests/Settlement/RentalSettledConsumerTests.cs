using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Services.Integration;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Settlement;

public sealed class RentalSettledConsumerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MatchingPaymentConfirmationCompletesOnlyAfterInventoryClosure_AndReplayIsHarmless()
    {
        var order = Returns.ReturnTests.NewOrder(1);
        var item = order.Items.Single();
        var unit = item.UnitAssignments.Single();
        Assert.True(item.RecordAssetReturns(item.SupplierId, Now.AddHours(-1), null,
            [new ReturnUnitData(unit.Id, UnitCondition.Damaged, "Damaged", [])], Now, item.SupplierId).IsSuccess);
        decimal amount = Math.Min(100_000m, item.DepositAmount);
        Assert.True(item.SubmitDeduction(item.SupplierId, amount, "Repair", [Guid.NewGuid()],
            [unit.AssetId], Now).IsSuccess);
        Assert.True(item.AcceptDeductionByRenter(order.RenterId, Now.AddHours(1), "accept").IsSuccess);
        string key = RentalOperationExecutor.ComputeOperationKey(RentalItem.SettleRentalAction, item.Id.ToString("N"));
        var request = new SettleRentalRequest(order.Id, item.Id, "invoice-1", "payment-1",
            item.SupplierId, item.DepositAmount, amount, item.DepositAmount - amount,
            "VND", item.DeductionProposal!.Id.ToString("D"), key);
        string receiptPayload = JsonSerializer.Serialize(new { providerRequestJson = JsonSerializer.Serialize(request) });
        order.AddOperationReceipt(RentalOperationReceipt.CreatePending(Guid.NewGuid(), order.Id, key,
            RentalItem.SettleRentalAction, "fingerprint", Now, targetItemId: item.Id, payloadJson: receiptPayload));
        var payload = new RentalSettledPayload(order.Id, item.Id, "invoice-1", "payment-1", "settlement-1",
            key, amount, item.DepositAmount - amount, "VND", null, Now.AddHours(2));
        var envelope = new KafkaMessageEnvelope<RentalSettledPayload>(Guid.NewGuid(), "RentalSettled", 1,
            "payment-service", order.Id.ToString("D"), Now.AddHours(2), null, null, payload);
        var inbox = new RentalInboxMessage(Guid.NewGuid(), envelope.MessageId, "RentalSettled", "payment-service",
            order.Id.ToString("D"), null, null, JsonSerializer.Serialize(envelope), Now.AddHours(2));
        var consumer = new RentalSettledConsumer(TimeProvider.System);

        var early = await consumer.ApplyRentalSettledStagedAsync(order, envelope, inbox);
        Assert.True(early.IsFailure);
        Assert.Equal(RentalItemStatus.PendingSettlement, item.Status);
        Assert.True(item.ConfirmDamagedUnitBlocked(unit.Id, "maintenance-1").IsSuccess);
        Assert.True(item.ConfirmReturnedUnitClosure(unit.Id, Now.AddHours(2), "maintenance-1",
            "inventory-1", Now.AddHours(2)).IsSuccess);
        Assert.True((await consumer.ApplyRentalSettledStagedAsync(order, envelope, inbox)).IsSuccess);
        Assert.Equal(RentalItemStatus.Completed, item.Status);
        Assert.True((await consumer.ApplyRentalSettledStagedAsync(order, envelope, inbox)).IsSuccess);

        var wrong = envelope with { Payload = payload with { FinalDeductionAmount = amount + 1 } };
        Assert.True((await consumer.ApplyRentalSettledStagedAsync(order, wrong, inbox)).IsFailure);
    }
}
