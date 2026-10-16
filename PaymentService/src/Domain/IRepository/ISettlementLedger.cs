using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Domain.IRepository;

public enum SettlementWriteOutcome { Created, Replayed }

public sealed record SettlementWriteResult(
    Guid SettlementId,
    string SettlementRef,
    SettlementWriteOutcome Outcome);

/// <summary>
/// Ghi toàn bộ settlement trong MỘT transaction (record + 3 bút toán + receipt).
/// - Cùng OperationKey, cùng payload: trả lại cùng SettlementRef (Replayed), không ghi thêm.
/// - Cùng OperationKey khác payload, hoặc khác key nhưng cùng RentalItemId: lỗi Conflict.
/// </summary>
public interface ISettlementLedger
{
    Task<Result<SettlementWriteResult>> RecordSettlementAsync(
        RentalItemSettlement settlement, CancellationToken ct);
}