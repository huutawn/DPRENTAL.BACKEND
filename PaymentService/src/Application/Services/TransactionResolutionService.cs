using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.PaymentService.Application.DTOs.Operations;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepository;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Application.Services;

public sealed class TransactionResolutionService(
    ITransactionResolutionRepository repository,
    IOptionsMonitor<PaymentMerchantOptions> merchantOptions,
    IOptionsMonitor<SePayOptions> sePayOptions) : ITransactionResolutionService
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

        var command = new ResolveTransactionCommand(
            transactionId, adminId, invoiceRef, operationKey, externalReference, reason, fingerprint);
        var result = await repository.ResolveAsync(command,
            (transaction, invoice, now) => ValidateAndCreateEvent(transaction, invoice, externalReference, now),
            cancellationToken);
        if (result.IsFailure) return result.Error;

        var value = result.Value;
        return Result.Success(new ResolveUnmatchedTransactionResponse(
            value.ResolutionId, value.TransactionId, value.InvoiceRef, value.PaymentRef,
            value.ResolvedAt, value.ResolvedBy, value.IsReplay));
    }

    private Result<OutboxMessage> ValidateAndCreateEvent(
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

        var paid = invoice.MarkPaid(transaction.PaymentRef, transaction.TransferAmount, invoice.Currency,
            transaction.TransactionDate);
        if (paid.IsFailure) return paid.Error;

        return Result.Success(PaymentOutboxFactory.CreateInvoicePaidOutboxMessage(
            invoice, occurredAt: now, paidAt: transaction.TransactionDate));
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
