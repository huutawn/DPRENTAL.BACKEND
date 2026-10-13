using System.Text.Json;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Application.Services;

internal static class OperationReceiptReplayHelper
{
    public static bool TryReplay<TResponse>(
        OperationReceipt receipt,
        string expectedOperationType,
        string expectedFingerprint,
        Func<TResponse, TResponse> replayFactory,
        out Result<TResponse> result)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(replayFactory);

        if (string.Equals(receipt.OperationType, expectedOperationType, StringComparison.Ordinal)
            && string.Equals(receipt.PayloadFingerprint, expectedFingerprint, StringComparison.Ordinal))
        {
            var parsed = JsonSerializer.Deserialize<TResponse>(receipt.ResultJson)!;
            result = Result.Success(replayFactory(parsed));
            return true;
        }

        result = Error.Conflict("DUPLICATE_OPERATION_KEY", "OperationKey đã tồn tại với payload hoặc nghiệp vụ khác.");
        return false;
    }
}
