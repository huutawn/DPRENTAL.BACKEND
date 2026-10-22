using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.PaymentService.Application.DTOs.Operations;
using Dprental.PaymentService.Application.Hub;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Application.Services.Integrity;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;
using Dprental.PaymentService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Application.Services;

public sealed class TransactionResolutionService(
    ITransactionResolutionRepository repository,
    IInvoiceRepository invoiceRepository,
    IUnitOfWork unitOfWork,
    IPaymentDataEncryptor encryptor,
    IPaymentHubNotifier notifier,
    IOptionsMonitor<PaymentMerchantOptions> merchantOptions,
    IOptionsMonitor<SePayOptions> sePayOptions,
    TimeProvider clock) : ITransactionResolutionService
{
    public async Task<Result<ResolveUnmatchedTransactionResponse>> ResolveAsync(
        Guid transactionId, Guid adminId, ResolveUnmatchedTransactionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (transactionId == Guid.Empty || adminId == Guid.Empty ||
            !Clean(request.InvoiceRef, 64) || !Clean(request.OperationKey, 64) ||
            !Clean(request.ExternalReference, 256) || !Clean(request.AuditReason, 500))
            return Error.Validation("Payment.ResolutionInvalid", "Thông tin đối soát không hợp lệ.");

        var invoiceRef = request.InvoiceRef.Trim();
        var operationKey = request.OperationKey.Trim();
        var externalReference = request.ExternalReference.Trim();
        var reason = request.AuditReason.Trim();
        var fingerprint = Fingerprint(transactionId, adminId, invoiceRef, externalReference, reason);

        // Fast-path replay check before opening transaction
        var existingResolution = await repository.FindResolutionByOperationKeyAsync(operationKey, cancellationToken);
        if (existingResolution is not null)
        {
            var existingTx = await repository.FindTransactionByIdAsync(transactionId, cancellationToken);
            return ReplayOrConflict(existingResolution, existingTx, transactionId, fingerprint);
        }

        await using var tx = await unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            // 1. Lock payment transaction first
            var bankTransaction = await repository.FindTransactionWithRowLockAsync(transactionId, cancellationToken);
            if (bankTransaction is null)
                return Error.NotFound("Payment.TransactionNotFound", "Không tìm thấy giao dịch.");

            // 2. Re-check replay under lock
            existingResolution = await repository.FindResolutionByOperationKeyAsync(operationKey, cancellationToken);
            if (existingResolution is not null)
                return ReplayOrConflict(existingResolution, bankTransaction, transactionId, fingerprint);

            if (await repository.ExistsResolutionByTransactionIdAsync(bankTransaction.Id, cancellationToken))
                return Error.Conflict("Payment.TransactionAlreadyResolved", "Giao dịch đã được đối soát.");

            if (bankTransaction.Status != PaymentTransactionStatus.Unmatched)
                return Error.Conflict("Payment.TransactionNotUnmatched", "Chỉ đối soát giao dịch UNMATCHED.");

            // 3. Lock invoice next, maintaining consistent locking order
            var invoice = await invoiceRepository.FindInvoiceWithRowLockAsync(invoiceRef, cancellationToken);
            if (invoice is null)
                return Error.NotFound("Payment.InvoiceNotFound", "Không tìm thấy hóa đơn.");

            // 4. Validate matching conditions
            var now = clock.GetUtcNow();
            var validationResult = ValidateMatching(bankTransaction, invoice, externalReference, now);
            if (validationResult.IsFailure) return validationResult.Error;

            // 5. Update invoice state to PAID
            var paid = invoice.MarkPaid(bankTransaction.PaymentRef, bankTransaction.TransferAmount, invoice.Currency, bankTransaction.TransactionDate);
            if (paid.IsFailure) return paid.Error;

            // 6. Build resolution record, outbox, journal entry linking attestation
            var resolution = PaymentTransactionResolution.Create(
                bankTransaction, invoice, adminId, operationKey, fingerprint, externalReference, reason, now);

            var outbox = PaymentOutboxFactory.CreateInvoicePaidOutboxMessage(
                invoice, occurredAt: now, paidAt: bankTransaction.TransactionDate);

            var prevInv = await invoiceRepository.GetLatestJournalEntryAsync(invoice.InvoiceRef, cancellationToken);
            long nextInvVersion = (prevInv?.Version ?? 0) + 1;
            string prevInvHash = prevInv?.EntryHash ?? CanonicalJournalHasher.GenesisHash;
            Guid eventId = Guid.NewGuid();
            Guid operationId = Guid.NewGuid();
            outbox.LinkAttestation(eventId, operationId);
            string outboxDigest = CanonicalJournalHasher.ComputeOutboxDigest(outbox.Payload);

            var claims = new List<CanonicalEntityClaim>
            {
                OperationalSnapshotEngine.CreateInvoiceClaim(invoice),
                OperationalSnapshotEngine.CreatePaymentTransactionClaim(bankTransaction),
                OperationalSnapshotEngine.CreateOutboxClaim(outbox)
            };

            var journal = JournalEntryFactory.CreateEntry(
                operationId: operationId,
                streamId: invoice.InvoiceRef,
                streamType: JournalStreamTypes.Invoice,
                version: nextInvVersion,
                eventId: eventId,
                eventType: JournalEventTypes.PaymentMatched,
                amount: invoice.TotalAmount,
                currency: invoice.Currency,
                status: invoice.Status,
                confirmationSource: JournalConfirmationSources.Admin,
                actor: $"ADMIN:{adminId:D}",
                timestampUtc: now,
                previousHash: prevInvHash,
                relatedEntities: new Dictionary<string, string?>
                {
                    ["paymentRef"] = bankTransaction.PaymentRef,
                    ["invoiceRef"] = invoice.InvoiceRef,
                    ["rentalOrderId"] = invoice.RentalOrderId.ToString("D"),
                    ["externalReference"] = externalReference,
                    ["operationKey"] = operationKey
                },
                snapshot: OperationalSnapshotEngine.CreateInvoiceSnapshot(invoice, invoice.Revision),
                entityClaims: claims,
                outboxDigest: outboxDigest,
                outboxMessageId: outbox.MessageId,
                paymentRef: bankTransaction.PaymentRef,
                invoiceRef: invoice.InvoiceRef,
                rentalOrderId: invoice.RentalOrderId,
                encryptor: encryptor);

            await repository.SaveResolutionAsync(resolution, journal, outbox, cancellationToken);
            await tx.CommitAsync(cancellationToken);

            // 7. Fire realtime notification after successful commit
            await notifier.NotifyPaymentCompletedAsync(invoice.RenterId, eventId, invoice, cancellationToken);

            return Result.Success(new ResolveUnmatchedTransactionResponse(
                resolution.Id, resolution.TransactionId, resolution.InvoiceRef, bankTransaction.PaymentRef,
                resolution.OccurredAt, resolution.ActorId, IsReplay: false));
        }
        catch (Exception ex) when (ex is StorageConstraintViolationException or DbUpdateException)
        {
            await tx.RollbackAsync(cancellationToken);
            var prior = await repository.FindResolutionByOperationKeyAsync(operationKey, cancellationToken);
            var bankTransaction = await repository.FindTransactionByIdAsync(transactionId, cancellationToken);
            if (prior is not null && bankTransaction is not null)
                return ReplayOrConflict(prior, bankTransaction, transactionId, fingerprint);
            return Error.Conflict("Payment.TransactionAlreadyResolved", "Giao dịch đã được đối soát.");
        }
    }

    private Result ValidateMatching(
        PaymentTransaction transaction, Invoice invoice, string externalReference, DateTimeOffset now)
    {
        if (!string.Equals(externalReference, transaction.ProviderTransactionId, StringComparison.Ordinal) &&
            !string.Equals(externalReference, transaction.ReferenceCode, StringComparison.Ordinal))
            return Error.Conflict("Payment.ExternalReferenceMismatch", "Mã tham chiếu không khớp giao dịch ngân hàng.");

        if (transaction.InvoiceId is { } linkedInvoiceId && linkedInvoiceId != invoice.Id)
            return Error.Conflict("Payment.TransactionInvoiceConflict", "Giao dịch đã gắn với hóa đơn khác.");
        if (!string.IsNullOrWhiteSpace(transaction.PaymentCode) &&
            !string.Equals(transaction.PaymentCode, invoice.PaymentCode, StringComparison.Ordinal))
            return Error.Conflict("Payment.PaymentCodeMismatch", "Mã thanh toán không khớp hóa đơn.");

        if (!string.Equals(transaction.TransferType, "in", StringComparison.OrdinalIgnoreCase) ||
            transaction.TransferAmount <= 0)
            return Error.Conflict("Payment.NotIncomingTransfer", "Giao dịch không phải tiền vào hợp lệ.");

        var merchant = merchantOptions.CurrentValue;
        var sePay = sePayOptions.CurrentValue;
        string? bank = MapBank(transaction.Gateway, merchant.Bank, sePay);
        if (bank is null ||
            !string.Equals(bank, invoice.MerchantBank, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(bank, merchant.Bank, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(transaction.AccountNumber, invoice.MerchantAccount, StringComparison.Ordinal) ||
            !string.Equals(transaction.AccountNumber, merchant.Account, StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(sePay.AccountNumber) &&
             !string.Equals(transaction.AccountNumber, sePay.AccountNumber, StringComparison.Ordinal)))
            return Error.Conflict("Payment.MerchantAccountMismatch", "Tài khoản nhận tiền không khớp cấu hình Payment.");

        if (!string.Equals(invoice.Currency, "VND", StringComparison.OrdinalIgnoreCase) ||
            transaction.TransferAmount != invoice.TotalAmount)
            return Error.Conflict("Payment.ResolutionAmountMismatch", "Số tiền giao dịch không khớp hóa đơn.");

        if (invoice.Status != InvoiceStatus.Pending ||
            transaction.TransactionDate >= invoice.PaymentDeadline || now >= invoice.PaymentDeadline)
            return Error.Conflict("Payment.ResolutionPastDeadline",
                "Hóa đơn không còn trong hạn thanh toán; giữ giao dịch để đối soát thủ công.");

        return Result.Success();
    }

    private static Result<ResolveUnmatchedTransactionResponse> ReplayOrConflict(
        PaymentTransactionResolution prior, PaymentTransaction? transaction, Guid transactionId, string fingerprint)
    {
        if (prior.TransactionId != transactionId ||
            prior.PayloadFingerprint != fingerprint)
            return Error.Conflict("Payment.ResolutionOperationKeyConflict",
                "OperationKey đã được dùng cho giao dịch hoặc nội dung khác.");
        return Result.Success(new ResolveUnmatchedTransactionResponse(
            prior.Id, prior.TransactionId, prior.InvoiceRef, transaction?.PaymentRef ?? string.Empty,
            prior.OccurredAt, prior.ActorId, IsReplay: true));
    }

    private static string? MapBank(string gateway, string merchantBank, SePayOptions options)
    {
        if (options.GatewayMapping.TryGetValue(gateway, out var mapped)) return mapped;
        if (!string.IsNullOrWhiteSpace(options.Gateway) &&
            string.Equals(gateway, options.Gateway, StringComparison.OrdinalIgnoreCase)) return merchantBank;
        if (string.Equals(gateway, merchantBank, StringComparison.OrdinalIgnoreCase)) return merchantBank;
        return null;
    }

    private static bool Clean(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= max && !value.Any(char.IsControl);

    private static string Fingerprint(Guid transactionId, Guid adminId, string invoiceRef,
        string externalReference, string reason)
    {
        var canonical = JsonSerializer.Serialize(new[] {
            "ResolveUnmatchedTransaction", transactionId.ToString("D"), adminId.ToString("D"),
            invoiceRef, externalReference, reason
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
