using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Common.Enums;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Repositories;

/// <summary>
/// EF Core implementation of IRentalOrderRepository.
/// </summary>
public sealed partial class RentalOrderRepository : IRentalOrderRepository
{
    private const string RequestRefundAction = "RequestRefund";

    private readonly RentalServiceDbContext _dbContext;

    public RentalOrderRepository(RentalServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<RentalOrder?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalOrders
            .Include(o => o.TermsAcceptance)
            .Include(o => o.SubOrders)
            .Include(o => o.Items)
                .ThenInclude(i => i.UnitAssignments)
            .Include(o => o.Items)
                .ThenInclude(i => i.HandoverRecord)
            .Include(o => o.Items)
                .ThenInclude(i => i.ReturnRecords)
            .Include(o => o.Items)
                .ThenInclude(i => i.ReturnRequests).ThenInclude(r => r.Units)
            .Include(o => o.Items)
                .ThenInclude(i => i.ReturnRequests).ThenInclude(r => r.Issues).ThenInclude(issue => issue.Units)
            .Include(o => o.Items)
                .ThenInclude(i => i.ReturnRequests).ThenInclude(r => r.Resolutions).ThenInclude(res => res.Units)
            .Include(o => o.Items)
                .ThenInclude(i => i.DeductionProposal!)
                    .ThenInclude(p => p.Dispute)
            .Include(o => o.StateTransitions)
            .Include(o => o.OperationReceipts)
            .Include(o => o.RefundConfirmations)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
    }

    public async Task<RentalOrder?> GetByOrderCodeAsync(string orderCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderCode)) return null;

        return await _dbContext.RentalOrders
            .Include(o => o.TermsAcceptance)
            .Include(o => o.SubOrders)
            .Include(o => o.Items)
                .ThenInclude(i => i.UnitAssignments)
            .Include(o => o.Items)
                .ThenInclude(i => i.HandoverRecord)
            .Include(o => o.Items)
                .ThenInclude(i => i.ReturnRecords)
            .Include(o => o.Items)
                .ThenInclude(i => i.DeductionProposal!)
                    .ThenInclude(p => p.Dispute)
            .Include(o => o.StateTransitions)
            .Include(o => o.OperationReceipts)
            .FirstOrDefaultAsync(o => o.OrderCode == orderCode.Trim(), cancellationToken);
    }

    public async Task<RentalOrder?> GetByCheckoutOperationIdAsync(string operationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId)) return null;

        return await _dbContext.RentalOrders
            .Include(o => o.TermsAcceptance)
            .Include(o => o.Items)
                .ThenInclude(i => i.UnitAssignments)
            .Include(o => o.StateTransitions)
            .Include(o => o.OperationReceipts)
            .FirstOrDefaultAsync(o => o.CheckoutOperationId == operationId.Trim(), cancellationToken);
    }

    public async Task<IReadOnlyList<RentalOrder>> GetByRenterIdAsync(Guid renterId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalOrders
            .Include(o => o.Items)
            .Where(o => o.RenterId == renterId)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RentalOrder>> GetBySupplierIdAsync(Guid supplierId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalOrders
            .Include(o => o.Items)
            .Where(o => o.Items.Any(i => i.SupplierId == supplierId))
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<RentalOrder> CreateAsync(RentalOrder order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        await _dbContext.RentalOrders.AddAsync(order, cancellationToken);
        return order;
    }

    public Task<bool> UpdateAsync(RentalOrder order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        // Application đã quyết định thay đổi nghiệp vụ; repository chỉ đăng ký cách lưu.
        // Tạm tắt DetectChanges để EF chưa tự nhận diện các entity con mới có GUID sẵn
        // thành entity đã tồn tại. SaveChanges sẽ detect lại sau khi đăng ký xong.
        bool autoDetectChanges = _dbContext.ChangeTracker.AutoDetectChangesEnabled;
        _dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var entry = _dbContext.Entry(order);
            if (entry.State == EntityState.Detached)
            {
                // Aggregate được tải từ context khác: gắn lại cả graph theo contract hiện có.
                // Không đi tiếp vào nhánh đăng ký child mới dành cho aggregate đang tracked.
                _dbContext.RentalOrders.Update(order);
                return Task.FromResult(true);
            }
            // Aggregate đang tracked: giữ nguyên trạng thái từng child; chỉ đánh dấu root.
            else if (entry.State == EntityState.Unchanged)
            {
                entry.State = EntityState.Modified;
            }
            // ReturnRequest có nhiều tầng con (unit, issue, resolution). Chúng có ID ngay
            // khi được tạo, nên cần chỉ rõ Added để EF INSERT thay vì thử UPDATE hàng chưa có.
            // Chỉ đánh dấu entity Detached; child đang tracked giữ nguyên trạng thái.
            foreach (var request in order.Items.SelectMany(i => i.ReturnRequests))
            {
                if (_dbContext.Entry(request).State == EntityState.Detached)
                    _dbContext.Entry(request).State = EntityState.Added;
                foreach (var unit in request.Units)
                    if (_dbContext.Entry(unit).State == EntityState.Detached)
                        _dbContext.Entry(unit).State = EntityState.Added;
                foreach (var issue in request.Issues)
                {
                    if (_dbContext.Entry(issue).State == EntityState.Detached)
                        _dbContext.Entry(issue).State = EntityState.Added;
                    foreach (var issueUnit in issue.Units)
                        if (_dbContext.Entry(issueUnit).State == EntityState.Detached)
                            _dbContext.Entry(issueUnit).State = EntityState.Added;
                }
                foreach (var resolution in request.Resolutions)
                {
                    if (_dbContext.Entry(resolution).State == EntityState.Detached)
                        _dbContext.Entry(resolution).State = EntityState.Added;
                    foreach (var resUnit in resolution.Units)
                        if (_dbContext.Entry(resUnit).State == EntityState.Detached)
                            _dbContext.Entry(resUnit).State = EntityState.Added;
                }
            }
            // Các chứng từ handover/return/deduction cũng được tạo trong Application.
            // Không Update toàn bộ graph: cách đó có thể biến child mới thành Modified.
            foreach (var record in order.Items.SelectMany(i => i.ReturnRecords))
            {
                if (_dbContext.Entry(record).State == EntityState.Detached)
                    _dbContext.Entry(record).State = EntityState.Added;
            }
            foreach (var handover in order.Items.Select(i => i.HandoverRecord).Where(h => h is not null))
            {
                if (_dbContext.Entry(handover!).State == EntityState.Detached)
                    _dbContext.Entry(handover!).State = EntityState.Added;
            }
            foreach (var proposal in order.Items.Select(i => i.DeductionProposal).Where(p => p is not null))
            {
                if (_dbContext.Entry(proposal!).State == EntityState.Detached)
                    _dbContext.Entry(proposal!).State = EntityState.Added;
            }
            foreach (var dispute in order.Items.Select(i => i.DeductionProposal?.Dispute).Where(d => d is not null))
            {
                if (_dbContext.Entry(dispute!).State == EntityState.Detached)
                    _dbContext.Entry(dispute!).State = EntityState.Added;
            }
            // Receipt vừa có thể được thêm, vừa có thể đổi kết quả khi retry.
            // StateTransition là lịch sử bổ sung: ở đây chỉ đăng ký các bản ghi mới.
            foreach (var receipt in order.OperationReceipts)
            {
                var rEntry = _dbContext.Entry(receipt);
                if (rEntry.State == EntityState.Detached)
                    rEntry.State = EntityState.Added;
                else if (rEntry.State == EntityState.Unchanged)
                    rEntry.State = EntityState.Modified;
            }
            foreach (var transition in order.StateTransitions)
            {
                if (_dbContext.Entry(transition).State == EntityState.Detached)
                    _dbContext.Entry(transition).State = EntityState.Added;
            }
            return Task.FromResult(true);
        }
        finally
        {
            // Khôi phục cả khi lỗi/return sớm để không ảnh hưởng lần SaveChanges tiếp theo.
            _dbContext.ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
        }
    }

    public Task<RentalOrder?> GetByIdWithDetailsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return GetByIdAsync(id, cancellationToken);
    }
    public async Task<RentalOrder?> GetForCheckoutPollingAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalOrders
            .AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.OperationReceipts)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
    }
    public async Task<Guid?> GetRenterIdByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalOrders
            .AsNoTracking()
            .Where(o => o.Id == orderId)
            .Select(o => (Guid?)o.RenterId)
            .FirstOrDefaultAsync(cancellationToken);
    }



    public async Task<Guid?> GetOrderIdByItemIdAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var item = await _dbContext.RentalItems
            .AsNoTracking()
            .Where(i => i.Id == itemId)
            .Select(i => new { i.RentalOrderId })
            .FirstOrDefaultAsync(cancellationToken);

        return item?.RentalOrderId;
    }

    public async Task<RentalOrder?> GetByItemIdWithDetailsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var orderHeader = await _dbContext.RentalItems
            .AsNoTracking()
            .Where(i => i.Id == itemId)
            .Select(i => new { i.RentalOrderId })
            .FirstOrDefaultAsync(cancellationToken);

        if (orderHeader == null)
        {
            return null;
        }

        return await GetByIdAsync(orderHeader.RentalOrderId, cancellationToken);
    }

    public async Task<(RentalOrder Order, RentalItem Item, ReturnRequest Request)?> GetReturnRequestAggregateAsync(
        Guid returnRequestId,
        CancellationToken cancellationToken = default)
    {
        var orderId = await _dbContext.ReturnRequests
            .Where(r => r.Id == returnRequestId)
            .Select(r => (Guid?)r.RentalItem!.RentalOrderId)
            .FirstOrDefaultAsync(cancellationToken);

        if (!orderId.HasValue) return null;

        var order = await GetByIdWithDetailsAsync(orderId.Value, cancellationToken);
        if (order is null) return null;

        foreach (var item in order.Items)
        {
            var req = item.ReturnRequests.FirstOrDefault(r => r.Id == returnRequestId);
            if (req != null)
            {
                return (order, item, req);
            }
        }

        return null;
    }

}
