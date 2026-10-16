using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Domain.IRepository;

/// <summary>
/// Ghi toàn bộ settlement (khoản supplier được hưởng và các khoản kế toán) trong MỘT thao tác.
/// Contract cho subtask 3, nơi triển khai nguyên tử bằng một transaction:
/// - Hoặc ghi đủ tất cả bút toán, hoặc không ghi gì.
/// - Idempotent theo <see cref="RentalItemSettlement.OperationKey"/>: gọi lại với cùng key
///   và cùng số tiền thì không ghi thêm; cùng key nhưng khác số tiền hoặc DecisionRef là xung đột.
/// </summary>
public interface ISettlementLedger
{
    Task RecordSettlementAsync(RentalItemSettlement settlement, CancellationToken ct);
}