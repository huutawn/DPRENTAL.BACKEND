using Dprental.PaymentService.Application.DTOs.Payout;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Application.Services.Integrity;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;

namespace Dprental.PaymentService.Application.Services;

public sealed class SupplierPayoutService : ISupplierPayoutService
{
    private const int MaxOperationKey = 64;
    private const int MaxTransferReference = 256;
    private const int MaxReason = 500;

    private readonly ISupplierPayoutRepository _repository;
    private readonly IInvoiceRepository _invoices;
    private readonly IPaymentDataEncryptor _encryptor;

    public SupplierPayoutService(ISupplierPayoutRepository repository, IInvoiceRepository invoices,
        IPaymentDataEncryptor encryptor)
    {
        _repository = repository;
        _invoices = invoices;
        _encryptor = encryptor;
    }

    // Actor cố định khi SePay báo tiền ra khớp mã DPO; CompletedBy/ActorId của audit không được là Guid.Empty.
    public static readonly Guid SePayWebhookActorId = new("5e9a0000-0000-0000-0000-000000000001");
    private const string SePayWebhookActor = "WEBHOOK:SEPAY";
    private const string SePayAuditReason = "SePay báo chuyển khoản payout khớp mã DPO và số tiền.";

    public Task<Result<CompleteSupplierPayoutResponse>> CompleteAsync(
        Guid payoutId, Guid adminId, CompleteSupplierPayoutRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return CompleteCoreAsync(payoutId, adminId, JournalConfirmationSources.Admin, $"ADMIN:{adminId:D}",
            request.OperationKey, request.ExternalTransferReference, request.AuditReason, ct);
    }

    // paymentRef (SEPAY:{id}) vừa là OperationKey vừa là mã chuyển khoản: SePay gửi lại webhook chỉ ra replay.
    public Task<Result<CompleteSupplierPayoutResponse>> CompleteFromBankAsync(
        Guid payoutId, string paymentRef, CancellationToken ct) =>
        CompleteCoreAsync(payoutId, SePayWebhookActorId, JournalConfirmationSources.Webhook, SePayWebhookActor,
            paymentRef, paymentRef, SePayAuditReason, ct);

    private async Task<Result<CompleteSupplierPayoutResponse>> CompleteCoreAsync(
        Guid payoutId, Guid actorId, string confirmationSource, string actorLabel,
        string operationKey, string externalTransferReference, string auditReason, CancellationToken ct)
    {
        if (Validate(payoutId, actorId, operationKey,
                externalTransferReference, "ExternalTransferReference", MaxTransferReference,
                auditReason) is { } invalid)
            return invalid;

        var result = await _repository.CompleteAsync(new CompleteSupplierPayoutCommand(
            payoutId, actorId, operationKey.Trim(),
            externalTransferReference.Trim(), auditReason.Trim()),
            async (payout, now, token) =>
            {
                var message = PaymentOutboxFactory.CreateSupplierPayoutCompleted(payout);
                var previous = await _invoices.GetLatestJournalEntryAsync(payout.PayoutRef, token);
                Guid operationId = Guid.NewGuid();
                Guid eventId = Guid.NewGuid();
                message.LinkAttestation(eventId, operationId);
                var journal = JournalEntryFactory.CreateEntry(operationId, payout.PayoutRef,
                    JournalStreamTypes.SupplierPayout, (previous?.Version ?? 0) + 1,
                    eventId, JournalEventTypes.SupplierPayoutCompleted, payout.Amount,
                    payout.Currency, payout.Status, confirmationSource,
                    actorLabel, now,
                    previous?.EntryHash ?? CanonicalJournalHasher.GenesisHash, _encryptor,
                    relatedEntities: new Dictionary<string, string?>
                    {
                        ["settlementId"] = payout.SettlementId.ToString("D"),
                        ["rentalItemId"] = payout.RentalItemId.ToString("D"),
                        ["supplierId"] = payout.SupplierId.ToString("D")
                    },
                    entityClaims: [OperationalSnapshotEngine.CreateOutboxClaim(message)],
                    outboxDigest: CanonicalJournalHasher.ComputeOutboxDigest(message.Payload),
                    outboxMessageId: message.MessageId, rentalItemId: payout.RentalItemId);
                return new SupplierPayoutPublication(message, journal);
            }, ct);

        if (!result.IsSuccess) return result.Error;

        var w = result.Value;
        return Result.Success(new CompleteSupplierPayoutResponse(
            w.PayoutId, w.PayoutRef, w.Status, operationKey.Trim(), w.ExternalTransferReference!,
            w.CompletedAt!.Value, w.CompletedBy!.Value, w.IsReplay));
    }

    private static Error? Validate(
        Guid payoutId, Guid adminId, string? operationKey,
        string? value, string valueName, int valueMax, string? reason)
    {
        if (payoutId == Guid.Empty)
            return Error.Validation("Payment.PayoutIdInvalid", "payoutId không hợp lệ.");
        if (adminId == Guid.Empty)
            return Error.Validation("Payment.AdminIdInvalid", "Không xác định được admin thực hiện.");
        if (!IsClean(operationKey, MaxOperationKey))
            return Error.Validation("Payment.OperationKeyInvalid", $"OperationKey bắt buộc, tối đa {MaxOperationKey} ký tự.");
        if (!IsClean(value, valueMax))
            return Error.Validation("Payment.PayoutFieldInvalid", $"{valueName} bắt buộc, tối đa {valueMax} ký tự.");
        if (!IsClean(reason, MaxReason))
            return Error.Validation("Payment.AuditReasonInvalid", $"AuditReason bắt buộc, tối đa {MaxReason} ký tự.");
        return null;
    }

    private static bool IsClean(string? s, int max) =>
        !string.IsNullOrWhiteSpace(s) && s.Trim().Length <= max && !s.Any(char.IsControl);
}
