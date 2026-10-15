using Dprental.PaymentService.Application.DTOs.SePay;
using Dprental.PaymentService.Common;

namespace Dprental.PaymentService.Application.Services;

internal static class SePayTransactionNormalizer
{
    public static Result<NormalizedBankTransaction> Normalize(SePayWebhookPayload? payload)
    {
        if (payload is null)
        {
            return Error.Validation("INVALID_PAYLOAD", "Webhook payload cannot be null.");
        }

        if (!payload.Id.HasValue || payload.Id.Value <= 0)
        {
            return Error.Validation("INVALID_ID", "SePay transaction id must be a positive integer.");
        }

        if (!payload.TransferAmount.HasValue || payload.TransferAmount.Value <= 0 ||
            decimal.Floor(payload.TransferAmount.Value) != payload.TransferAmount.Value)
        {
            return Error.Validation("INVALID_TRANSFER_AMOUNT", "Transfer amount must be a positive whole integer.");
        }

        if (string.IsNullOrWhiteSpace(payload.Gateway))
        {
            return Error.Validation("INVALID_GATEWAY", "Gateway cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(payload.AccountNumber))
        {
            return Error.Validation("INVALID_ACCOUNT_NUMBER", "AccountNumber cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(payload.TransferType))
        {
            return Error.Validation("INVALID_TRANSFER_TYPE", "TransferType cannot be empty.");
        }

        if (!SePayDateTimeParser.TryParseVietnamTransactionDate(payload.TransactionDate, out var utcTransactionDate))
        {
            return Error.Validation("INVALID_TRANSACTION_DATE", "TransactionDate is required and must be a valid datetime.");
        }

        return new NormalizedBankTransaction(
            Id: payload.Id.Value,
            Gateway: payload.Gateway.Trim(),
            TransactionDate: utcTransactionDate,
            AccountNumber: payload.AccountNumber.Trim(),
            SubAccount: payload.SubAccount?.Trim(),
            Code: payload.Code,
            Content: payload.Content,
            TransferType: payload.TransferType.Trim().ToLowerInvariant(),
            Description: payload.Description,
            TransferAmount: payload.TransferAmount.Value,
            Accumulated: payload.Accumulated,
            ReferenceCode: payload.ReferenceCode?.Trim());
    }
}
