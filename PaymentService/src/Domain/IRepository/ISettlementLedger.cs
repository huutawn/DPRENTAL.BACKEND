using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Domain.IRepository;

public enum SettlementWriteOutcome { Created, Replayed }

public sealed record SettlementWriteResult(
    Guid SettlementId,
    string SettlementRef,
    SettlementWriteOutcome Outcome,
    SettlementState State,
    string? ManualRefundRef);

/// <summary>
/// Ghi toàn bộ settlement trong MỘT transaction: record, 3 bút toán, receipt, và
/// - ManualRefundRequirement (PENDING_MANUAL_REFUND) nếu DepositRefundDue > 0,
/// - hoặc tin RentalSettled vào outbox nếu không cần hoàn cọc.
/// Replay trả trạng thái hiện tại. Xung đột trả Error.Conflict.
/// </summary>
public interface ISettlementLedger
{
    Task<Result<SettlementWriteResult>> RecordSettlementAsync(
        RentalItemSettlement settlement, CancellationToken ct);
}