using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Repositories;

public sealed class RentalRefundRepository(RentalServiceDbContext dbContext) : IRentalRefundRepository
{
    private const string RequestRefundAction = "RequestRefund";

    public async Task<RecordPage<RefundQueueRecord>> GetQueuePageAsync(
        bool? completed,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        var query =
            from receipt in dbContext.RentalOperationReceipts.AsNoTracking()
            where receipt.TargetItemId != null
                && (receipt.Action == RequestRefundAction || receipt.Action == RentalItem.SettleRentalAction)
            join item in dbContext.RentalItems.AsNoTracking() on receipt.TargetItemId equals (Guid?)item.Id
            where receipt.Action == RequestRefundAction
                || item.DepositAmount - (item.FinalDeductionAmount ?? 0m) > 0m
            join confirmation in dbContext.RentalRefundConfirmations.AsNoTracking()
                on item.Id equals confirmation.RentalItemId into confirmations
            from confirmation in confirmations.DefaultIfEmpty()
            select new { receipt, item, order = item.RentalOrder!, confirmation };

        if (completed.HasValue)
        {
            query = completed.Value
                ? query.Where(x => x.confirmation != null)
                : query.Where(x => x.confirmation == null);
        }

        int totalCount = await query.CountAsync(cancellationToken);

        query = completed == false
            ? query.OrderBy(x => x.receipt.CreatedAt).ThenBy(x => x.receipt.Id)
            : query.OrderByDescending(x => x.receipt.CreatedAt).ThenByDescending(x => x.receipt.Id);

        List<RefundQueueRecord> items = await query
            .Skip(skip)
            .Take(take)
            .Select(x => new RefundQueueRecord(
                x.item.Id,
                x.order.Id,
                x.order.OrderCode,
                x.order.RenterId,
                x.order.RecipientName,
                x.order.RecipientPhone,
                x.order.RefundDestinationRef,
                x.item.ListingTitleSnapshot,
                x.item.SupplierId,
                x.item.Status,
                x.receipt.Action,
                x.receipt.Status,
                x.item.RentalAmount,
                x.item.DepositAmount,
                x.item.FinalDeductionAmount,
                x.receipt.CreatedAt,
                x.confirmation == null
                    ? null
                    : new RefundConfirmationRecord(
                        x.confirmation.RefundAmount,
                        x.confirmation.BankCode,
                        x.confirmation.AccountNumber,
                        x.confirmation.AccountHolderName,
                        x.confirmation.BankTransactionRef,
                        x.confirmation.Note,
                        x.confirmation.ConfirmedBy,
                        x.confirmation.ConfirmedAt)))
            .ToListAsync(cancellationToken);

        return new RecordPage<RefundQueueRecord>(items, totalCount);
    }

    public Task<bool> IsBankTransactionRefUsedAsync(string bankTransactionRef, CancellationToken cancellationToken = default) =>
        dbContext.RentalRefundConfirmations
            .AsNoTracking()
            .AnyAsync(c => c.BankTransactionRef == bankTransactionRef, cancellationToken);
}
