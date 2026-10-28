using Dprental.PaymentService.Api.Tests.TestHelpers;
using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.Repositories;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace Dprental.PaymentService.Api.Tests.Integration;

public sealed class SettlementLedgerPersistenceTests
{
    [Fact]
    public async Task Paid_invoice_creates_three_entries_and_replays_without_duplicates()
    {
        await using var h = await FinancialWorkflowHarness.CreateAsync();
        var invoice = await h.SeedAsync();
        await using var db = new PaymentServiceDbContext(h.PaymentOptions);
        var request = FinancialWorkflowHarness.SettlementRequest(invoice);
        var result = await h.Settlement(db).SettleAsync(request, default);
        Assert.True(result.IsSuccess, result.Error.Message);
        var replay = await h.Settlement(db).SettleAsync(request, default);
        Assert.True(replay.IsSuccess, replay.Error.Message);
        Assert.True(replay.Value.IsReplay);
        var record = await db.RentalItemSettlements.Include(x => x.Entries).SingleAsync();
        Assert.Equal(10_000, record.PlatformFeeAmount); Assert.Equal(70_000, record.DepositRefundDue);
        Assert.Equal(110_000, record.SupplierPayable); Assert.Equal(3, record.Entries.Count);
        Assert.Equal(SettlementState.PendingManualRefund, record.Status); Assert.Null(record.CompletedAt);
        var refund = await db.ManualRefundRequirements.Include(x => x.Items).SingleAsync();
        Assert.Equal(70_000, refund.Amount); Assert.Equal(ManualRefundStatus.PendingManualRefund, refund.Status);
        Assert.Equal(record.RentalItemId, refund.RentalItemId); Assert.Single(refund.Items);
        Assert.Empty(await db.OutboxMessages.Where(x => x.MessageType == "RentalSettled").ToListAsync());
        var requested = await db.OutboxMessages.SingleAsync(x => x.MessageType == OutboxTopics.RefundRequestedFinancialMessageType);
        Assert.Equal(OutboxTopics.PaymentAnalyticsV1, requested.Topic);
        using (var json = JsonDocument.Parse(requested.Payload))
        {
            var payload = json.RootElement.GetProperty("payload");
            Assert.Equal(refund.RequirementRef, payload.GetProperty("refundRef").GetString());
            Assert.Equal(FinancialRefundKinds.SettlementDepositRefund, payload.GetProperty("refundKind").GetString());
            Assert.Equal(70_000m, payload.GetProperty("amount").GetDecimal());
            Assert.False(payload.TryGetProperty("accountNumber", out _));
        }
        Assert.Equal(3, await db.TransactionJournals.CountAsync(x => x.StreamId == record.SettlementRef || x.StreamId == refund.RequirementRef));
    }

    [Fact]
    public async Task Full_deduction_completes_and_writes_one_rental_settled_event()
    {
        await using var h = await FinancialWorkflowHarness.CreateAsync();
        var invoice = await h.SeedAsync();
        await using var db = new PaymentServiceDbContext(h.PaymentOptions);
        var request = FinancialWorkflowHarness.SettlementRequest(invoice, 90_000);
        var result = await h.Settlement(db).SettleAsync(request, default);
        Assert.True(result.IsSuccess, result.Error.Message); Assert.Equal(SettlementStatus.Completed, result.Value.Status);
        var replay = await h.Settlement(db).SettleAsync(request, default);
        Assert.True(replay.IsSuccess); Assert.True(replay.Value.IsReplay);
        Assert.Empty(await db.ManualRefundRequirements.ToListAsync());
        var message = await db.OutboxMessages.SingleAsync(x => x.MessageType == "RentalSettled");
        var journal = await db.TransactionJournals.SingleAsync(x => x.EventId == message.EventId);
        Assert.Equal(message.MessageId, journal.OutboxMessageId);
        Assert.Equal(CanonicalJournalHasher.ComputeOutboxDigest(message.Payload), journal.OutboxDigest);
        var financial = await db.OutboxMessages.SingleAsync(x => x.MessageType == OutboxTopics.RentalItemFinancialSettledMessageType);
        Assert.Equal(OutboxTopics.PaymentAnalyticsV1, financial.Topic);
        using (var json = JsonDocument.Parse(financial.Payload))
        {
            var payload = json.RootElement.GetProperty("payload");
            Assert.Equal(90_000m, payload.GetProperty("finalDeductionAmount").GetDecimal());
            Assert.Equal(0m, payload.GetProperty("depositRefundDue").GetDecimal());
            Assert.Equal(10_000m, payload.GetProperty("platformFeeAmount").GetDecimal());
            Assert.Equal(180_000m, payload.GetProperty("supplierPayable").GetDecimal());
            Assert.False(payload.TryGetProperty("accountNumber", out _));
        }
        var unpublished = await h.Checker().VerifyAsync(new TransactionVerificationRequest(
            financial.MessageId.ToString("D"), VerificationPurposes.OutboxPublication, "before-confirmation",
            ExpectedOutboxDigest: CanonicalJournalHasher.ComputeOutboxDigest(financial.Payload),
            Topic: financial.Topic, MessageKey: financial.AggregateId, OutboxMessageId: financial.MessageId));
        Assert.True(unpublished.IsSuccess);
        Assert.Null(unpublished.Value.PublicationProof);
        await h.ConfirmAsync();
        var proof = await h.Checker().VerifyAsync(new TransactionVerificationRequest(
            financial.MessageId.ToString("D"), VerificationPurposes.OutboxPublication, "after-confirmation",
            ExpectedOutboxDigest: CanonicalJournalHasher.ComputeOutboxDigest(financial.Payload),
            Topic: financial.Topic, MessageKey: financial.AggregateId, OutboxMessageId: financial.MessageId));
        Assert.True(proof.IsSuccess, proof.Error.Message);
        Assert.NotNull(proof.Value.PublicationProof);
        using var proofJson = JsonDocument.Parse(proof.Value.PublicationProof.Payload);
        Assert.Equal(OutboxTopics.AnalyticsAudience, proofJson.RootElement.GetProperty("audience").GetString());
    }

    [Fact]
    public async Task Concurrent_manual_refund_confirmations_complete_once_and_emit_one_event()
    {
        await using var h = await FinancialWorkflowHarness.CreateAsync();
        var invoice = await h.SeedAsync();
        Guid requirementId;
        await using (var db = new PaymentServiceDbContext(h.PaymentOptions))
        {
            var settlement = await h.Settlement(db).SettleAsync(FinancialWorkflowHarness.SettlementRequest(invoice), default);
            Assert.True(settlement.IsSuccess, settlement.Error.Message);
            requirementId = await db.ManualRefundRequirements.Select(x => x.Id).SingleAsync();
        }
        await h.ConfirmAsync();
        var admin = Guid.NewGuid();
        async Task<Dprental.PaymentService.Common.Result<CompleteManualRefundResponse>> Confirm(string reference)
        {
            await using var db = new PaymentServiceDbContext(h.PaymentOptions);
            return await h.Refund(db).CompleteAsync(requirementId, new(reference, "confirm-refund", "Đã xác nhận chuyển khoản hoàn tiền."), admin, default);
        }
        var results = await Task.WhenAll(Confirm("BANK-123"), Confirm("BANK-123"));
        Assert.All(results, x => Assert.True(x.IsSuccess, x.Error.Message));
        Assert.Single(results, x => x.Value.IsReplay); Assert.Single(results, x => !x.Value.IsReplay);
        Assert.Equal("DUPLICATE_OPERATION_KEY", (await Confirm("OTHER")).Error.Code);
        await using var verify = new PaymentServiceDbContext(h.PaymentOptions);
        Assert.Equal(ManualRefundStatus.Completed, (await verify.ManualRefundRequirements.SingleAsync()).Status);
        Assert.Equal(SettlementState.Completed, (await verify.RentalItemSettlements.SingleAsync()).Status);
        Assert.Single(await verify.OutboxMessages.Where(x => x.MessageType == "RentalSettled").ToListAsync());
        var refundFinancial = await verify.OutboxMessages.SingleAsync(x => x.MessageType == OutboxTopics.RefundCompletedFinancialMessageType);
        var settlementFinancial = await verify.OutboxMessages.SingleAsync(x => x.MessageType == OutboxTopics.RentalItemFinancialSettledMessageType);
        using (var json = JsonDocument.Parse(refundFinancial.Payload))
        {
            var payload = json.RootElement.GetProperty("payload");
            Assert.Equal(FinancialRefundKinds.SettlementDepositRefund, payload.GetProperty("refundKind").GetString());
            Assert.Equal(70_000m, payload.GetProperty("amount").GetDecimal());
        }
        Assert.NotEqual(refundFinancial.MessageId, settlementFinancial.MessageId);
        await h.ConfirmAsync();
    }

    [Theory]
    [InlineData(FinancialRefundKinds.CancellationRefund, false)]
    [InlineData(FinancialRefundKinds.LatePaymentRefund, true)]
    public async Task Rental_refund_emits_classified_financial_event_only_after_manual_transfer(
        string refundKind, bool late)
    {
        await using var h = await FinancialWorkflowHarness.CreateAsync();
        var invoice = await h.SeedAsync(late: late);
        var item = Assert.Single(invoice.Items);
        var request = new RequestRefundRequest(invoice.RentalOrderId, invoice.InvoiceRef,
            invoice.PaymentRef!, [new RefundItemRequest(item.RentalItemId, item.RentalAmount, item.DepositAmount)],
            "Refund after rental decision", Guid.NewGuid().ToString("N"), refundKind);
        Guid requirementId;
        await using (var db = new PaymentServiceDbContext(h.PaymentOptions))
        {
            var requested = await h.Refund(db).RequestAsync(request, default);
            Assert.True(requested.IsSuccess, requested.Error.Message);
            var replay = await h.Refund(db).RequestAsync(request, default);
            Assert.True(replay.IsSuccess, replay.Error.Message);
            Assert.True(replay.Value.IsReplay);
            requirementId = await db.ManualRefundRequirements.Select(x => x.Id).SingleAsync();
            Assert.Empty(await db.OutboxMessages.Where(x => x.MessageType == OutboxTopics.RefundCompletedFinancialMessageType).ToListAsync());
            var pending = await db.OutboxMessages.SingleAsync(x => x.MessageType == OutboxTopics.RefundRequestedFinancialMessageType);
            Assert.Equal(OutboxTopics.PaymentAnalyticsV1, pending.Topic);
            using var json = JsonDocument.Parse(pending.Payload);
            var payload = json.RootElement.GetProperty("payload");
            Assert.Equal(refundKind, payload.GetProperty("refundKind").GetString());
            Assert.Equal(invoice.TotalAmount, payload.GetProperty("amount").GetDecimal());
            Assert.False(payload.TryGetProperty("accountNumber", out _));
        }
        await h.ConfirmAsync();
        var completion = new CompleteManualRefundRequest("BANK-REFUND-123", Guid.NewGuid().ToString("N"), "Transfer confirmed");
        await using (var db = new PaymentServiceDbContext(h.PaymentOptions))
        {
            var result = await h.Refund(db).CompleteAsync(requirementId, completion, Guid.NewGuid(), default);
            Assert.True(result.IsSuccess, result.Error.Message);
        }
        await using var verify = new PaymentServiceDbContext(h.PaymentOptions);
        var eventMessage = await verify.OutboxMessages.SingleAsync(x => x.MessageType == OutboxTopics.RefundCompletedFinancialMessageType);
        Assert.Equal(OutboxTopics.PaymentAnalyticsV1, eventMessage.Topic);
        using (var json = JsonDocument.Parse(eventMessage.Payload))
        {
            var payload = json.RootElement.GetProperty("payload");
            Assert.Equal(refundKind, payload.GetProperty("refundKind").GetString());
            Assert.Equal(invoice.RentalOrderId, payload.GetProperty("rentalOrderId").GetGuid());
            Assert.Equal(invoice.TotalAmount, payload.GetProperty("amount").GetDecimal());
            Assert.False(payload.TryGetProperty("externalTransferReference", out _));
            Assert.False(payload.TryGetProperty("accountNumber", out _));
        }
        await h.ConfirmAsync();
        var proof = await h.Checker().VerifyAsync(new TransactionVerificationRequest(
            eventMessage.MessageId.ToString("D"), VerificationPurposes.OutboxPublication, "refund-proof",
            ExpectedOutboxDigest: CanonicalJournalHasher.ComputeOutboxDigest(eventMessage.Payload),
            Topic: eventMessage.Topic, MessageKey: eventMessage.AggregateId, OutboxMessageId: eventMessage.MessageId));
        Assert.True(proof.IsSuccess, proof.Error.Message);
        Assert.NotNull(proof.Value.PublicationProof);
        using var proofJson = JsonDocument.Parse(proof.Value.PublicationProof.Payload);
        Assert.Equal(OutboxTopics.AnalyticsAudience, proofJson.RootElement.GetProperty("audience").GetString());
    }

    [Fact]
    public async Task Conflicting_decision_or_operation_key_does_not_write_again()
    {
        await using var h = await FinancialWorkflowHarness.CreateAsync();
        var invoice = await h.SeedAsync();
        await using var db = new PaymentServiceDbContext(h.PaymentOptions);
        var request = FinancialWorkflowHarness.SettlementRequest(invoice);
        Assert.True((await h.Settlement(db).SettleAsync(request, default)).IsSuccess);
        var conflict = await h.Settlement(db).SettleAsync(request with { FinalDeductionAmount = 30_000, RefundedDepositAmount = 60_000 }, default);
        Assert.Equal("DUPLICATE_OPERATION_KEY", conflict.Error.Code);
        var other = await h.Settlement(db).SettleAsync(request with { OperationKey = "other" }, default);
        Assert.Equal("DUPLICATE_SETTLEMENT", other.Error.Code);
        Assert.Equal(3, await db.SettlementLedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Unpaid_invoice_cannot_be_settled()
    {
        await using var h = await FinancialWorkflowHarness.CreateAsync();
        var invoice = await h.SeedAsync(paid: false);
        await using var db = new PaymentServiceDbContext(h.PaymentOptions);
        var request = FinancialWorkflowHarness.SettlementRequest(invoice) with { PaymentRef = "UNPAID" };
        Assert.True((await h.Settlement(db).SettleAsync(request, default)).IsFailure);
        Assert.Empty(await db.RentalItemSettlements.ToListAsync());
    }

    [Fact]
    public async Task Concurrent_retry_creates_only_one_settlement()
    {
        await using var h = await FinancialWorkflowHarness.CreateAsync();
        var invoice = await h.SeedAsync();
        var request = FinancialWorkflowHarness.SettlementRequest(invoice);
        async Task<Dprental.PaymentService.Common.Result<SettleRentalResponse>> Write()
        {
            await using var db = new PaymentServiceDbContext(h.PaymentOptions);
            return await h.Settlement(db).SettleAsync(request, default);
        }
        var results = await Task.WhenAll(Write(), Write());
        Assert.All(results, x => Assert.True(x.IsSuccess, x.Error.Message));
        Assert.Single(results, x => !x.Value.IsReplay);
        await using var verify = new PaymentServiceDbContext(h.PaymentOptions);
        Assert.Equal(1, await verify.RentalItemSettlements.CountAsync());
        Assert.Equal(3, await verify.SettlementLedgerEntries.CountAsync());
    }
}
