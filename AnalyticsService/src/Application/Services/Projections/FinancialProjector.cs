using Dprental.AnalyticsService.Application.Errors;
using Dprental.AnalyticsService.Application.Kafka.Contracts;
using Dprental.AnalyticsService.Application.Kafka.Contracts.Payment;
using Dprental.AnalyticsService.Common;
using Dprental.AnalyticsService.Common.Utils;
using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.IRepositories;

namespace Dprental.AnalyticsService.Application.Services.Projections;

/// <summary>
/// Chiếu event của Payment vào bảng tài chính. Dữ liệu tài chính chỉ ghi một lần theo khóa tự nhiên:
/// cùng khóa + cùng số liệu là gửi lại (bỏ qua); cùng khóa + khác số liệu là xung đột (báo lỗi, không ghi đè).
/// Analytics không tự tính lại số liệu quyết toán. Không gọi SaveChanges.
/// </summary>
public sealed class FinancialProjector
{
    private readonly IFinancialFactRepository _repository;

    public FinancialProjector(IFinancialFactRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<Result> ApplyAsync(RentalSettledPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!string.Equals(payload.Currency, ProjectionErrors.SupportedCurrency, StringComparison.Ordinal))
        {
            return Result.Failure(ProjectionErrors.CurrencyInvalid(payload.Currency));
        }

        Result<long> deduction = VndAmount.ToLong(payload.FinalDeductionAmount);
        if (deduction.IsFailure)
        {
            return Result.Failure(deduction.Error);
        }

        Result<long> refundDue = VndAmount.ToLong(payload.RefundedDepositAmount);
        if (refundDue.IsFailure)
        {
            return Result.Failure(refundDue.Error);
        }

        FinancialSettlementFact incoming = new()
        {
            SettlementRef = payload.SettlementRef,
            RentalItemId = payload.RentalItemId,
            RentalOrderId = payload.RentalOrderId,
            FinalDeductionAmount = deduction.Value,
            DepositRefundDue = refundDue.Value, // Cọc PHẢI hoàn, không phải đã hoàn
            CompletedAt = payload.CompletedAt.ToUniversalTime()
        };

        FinancialSettlementFact? existing = await _repository.FindSettlementAsync(payload.SettlementRef, cancellationToken);
        if (existing is null)
        {
            _repository.AddSettlement(incoming);
            return Result.Success();
        }

        return SameSettlement(existing, incoming)
            ? Result.Success() // Payment gửi lại cùng quyết toán → bỏ qua, tổng không đổi
            : Result.Failure(ProjectionErrors.FactConflict("Settlement", payload.SettlementRef));
    }

    public async Task<Result> ApplyAsync(RefundCompletedPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!string.Equals(payload.Currency, ProjectionErrors.SupportedCurrency, StringComparison.Ordinal))
        {
            return Result.Failure(ProjectionErrors.CurrencyInvalid(payload.Currency));
        }

        DateTimeOffset completedAt = payload.CompletedAt.ToUniversalTime();

        // 1. Kiểm tra toàn bộ item (tiền, trùng, xung đột) trước khi thêm dòng nào
        List<RefundFact> toAdd = [];
        HashSet<Guid> seenItemIds = [];
        foreach (RefundCompletedItemPayload item in payload.Items)
        {
            if (!seenItemIds.Add(item.RentalItemId))
            {
                return Result.Failure(ProjectionErrors.DuplicateItem(AnalyticsMessageTypes.RefundCompleted, item.RentalItemId));
            }

            Result<long> rental = VndAmount.ToLong(item.RefundedRentalAmount);
            if (rental.IsFailure)
            {
                return Result.Failure(rental.Error);
            }

            Result<long> deposit = VndAmount.ToLong(item.RefundedDepositAmount);
            if (deposit.IsFailure)
            {
                return Result.Failure(deposit.Error);
            }

            RefundFact incoming = new()
            {
                RefundRef = payload.RefundRef,
                RentalItemId = item.RentalItemId,
                OrderId = payload.RentalOrderId,
                RefundedRentalAmount = rental.Value,
                RefundedDepositAmount = deposit.Value,
                CompletedAt = completedAt
            };

            RefundFact? existing = await _repository.FindRefundAsync(payload.RefundRef, item.RentalItemId, cancellationToken);
            if (existing is null)
            {
                toAdd.Add(incoming);
            }
            else if (!SameRefund(existing, incoming))
            {
                return Result.Failure(ProjectionErrors.FactConflict("Refund", $"{payload.RefundRef}/{item.RentalItemId}"));
            }
            // existing giống hệt → gửi lại, bỏ qua
        }

        // 2. Hợp lệ hết mới thêm
        foreach (RefundFact refund in toAdd)
        {
            _repository.AddRefund(refund);
        }

        return Result.Success();
    }

    private static bool SameSettlement(FinancialSettlementFact a, FinancialSettlementFact b) =>
        a.RentalItemId == b.RentalItemId
        && a.RentalOrderId == b.RentalOrderId
        && a.FinalDeductionAmount == b.FinalDeductionAmount
        && a.DepositRefundDue == b.DepositRefundDue
        && a.CompletedAt == b.CompletedAt;

    private static bool SameRefund(RefundFact a, RefundFact b) =>
        a.OrderId == b.OrderId
        && a.RefundedRentalAmount == b.RefundedRentalAmount
        && a.RefundedDepositAmount == b.RefundedDepositAmount
        && a.CompletedAt == b.CompletedAt;
}
