#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Operations;

public sealed partial class RentalOperationExecutor
{
    internal static T? ReadStoredProviderRequest<T>(string? payloadJson) where T : class
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("providerRequestJson", out var prov) &&
                prov.ValueKind == JsonValueKind.String)
                return JsonSerializer.Deserialize<T>(prov.GetString()!, JsonOptions);
            return JsonSerializer.Deserialize<T>(payloadJson, JsonOptions);
        }
        catch (JsonException) { return null; }
    }

    internal async Task<Result> SettleRentalCoreAsync(
        RentalOrder order, RentalItem item, RentalOperationReceipt receipt, CancellationToken ct)
    {
        if (receipt.IsSuccess && receipt.Status == RentalOperationReceipt.StatusSucceeded) return Result.Success();

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();

        // Consumer đã hoàn tất item trước khi receipt kịp chốt: đóng receipt, không gọi Payment nữa.
        if (item.Status == RentalItemStatus.Completed)
        {
            receipt.MarkSucceeded(utcNow);
            await CommitLocalChangesAsync(order, ct);
            return Result.Success();
        }

        // Dưới khóa Order: chưa đủ điều kiện thì chờ, không đốt lượt retry.
        if (item.Status != RentalItemStatus.PendingSettlement ||
            !item.FinalDeductionAmount.HasValue || !item.InventoryClosureConfirmedAt.HasValue)
        {
            _logger.LogInformation("SettleRental {OpKey} chờ số khấu trừ cuối và xác nhận đóng allocation", receipt.OperationId);
            return Result.Success();
        }

        receipt.BeginProviderAttempt(utcNow);
        await CommitLocalChangesAsync(order, ct);

        var request = ReadStoredProviderRequest<SettleRentalRequest>(receipt.PayloadJson);
        if (request is null)
        {
            receipt.MarkFailed("CORRUPTED_REQUEST_PAYLOAD", "Payload SettleRental bị hỏng hoặc thiếu; cần đối soát.",
                utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
            await CommitLocalChangesAsync(order, ct);
            return Error.Failure("CORRUPTED_REQUEST_PAYLOAD", "Payload SettleRental bị hỏng hoặc thiếu.");
        }

        var result = await _paymentClient.SettleRentalAsync(request, ct);

        utcNow = _timeProvider.GetUtcNow();
        if (result.IsSuccess)
        {
            var ack = result.Value;
            if (!string.Equals(ack.Status, "Accepted", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ack.OperationKey, receipt.OperationId, StringComparison.OrdinalIgnoreCase) ||
                ack.FinalDeductionAmount != request.FinalDeductionAmount ||
                ack.RefundedDepositAmount != request.RefundedDepositAmount ||
                !string.Equals(ack.Currency, request.Currency, StringComparison.OrdinalIgnoreCase))
            {
                RecordExecutionFailure(receipt,
                    Error.Validation("INVALID_SETTLE_RESPONSE", "Phản hồi từ PaymentService không hợp lệ."),
                    utcNow, forceUncertain: true);
                await CommitLocalChangesAsync(order, ct);
                return Error.Unprocessable("INVALID_SETTLE_RESPONSE", "Phản hồi quyết toán từ PaymentService không hợp lệ.");
            }

            // Accepted chỉ là ACK. Item chỉ COMPLETED khi consumer nhận RentalSettled.
            receipt.MarkSucceeded(utcNow, JsonSerializer.Serialize(ack, JsonOptions));
            await CommitLocalChangesAsync(order, ct);
            return Result.Success();
        }

        RecordExecutionFailure(receipt, result.Error, utcNow);
        await CommitLocalChangesAsync(order, ct);
        return result.Error;
    }
}
