using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.IRepositories;

namespace Dprental.AnalyticsService.Application.Projections;

/// <summary>
/// Ghi lại những gì projector đã chạm tới khi xử lý MỘT message (scoped). Sau khi lưu DB thành công,
/// dùng để biết cần báo SignalR cho NCC nào.
/// </summary>
public sealed class ReadModelChangeSet
{
    private readonly List<RentalItemFact> _items = [];
    private readonly HashSet<Guid> _suppliers = [];
    private readonly HashSet<Guid> _orderIds = [];
    private readonly HashSet<Guid> _itemIds = [];

    public void TrackItem(RentalItemFact item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
        if (item.RentalOrderId != Guid.Empty) _orderIds.Add(item.RentalOrderId);
        if (item.RentalItemId != Guid.Empty) _itemIds.Add(item.RentalItemId);
        if (item.SupplierId != Guid.Empty) _suppliers.Add(item.SupplierId);
    }

    public void TrackSupplier(Guid? supplierId)
    {
        if (supplierId is { } id && id != Guid.Empty)
        {
            _suppliers.Add(id);
        }
    }

    public void TrackOrder(Guid? orderId)
    {
        if (orderId is { } id && id != Guid.Empty)
        {
            _orderIds.Add(id);
        }
    }

    public void TrackItem(Guid? itemId)
    {
        if (itemId is { } id && id != Guid.Empty)
        {
            _itemIds.Add(id);
        }
    }

    public async Task ResolveSuppliersAsync(IRentalFactRepository repository, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        foreach (var item in _items)
        {
            if (item.SupplierId != Guid.Empty)
            {
                _suppliers.Add(item.SupplierId);
            }
        }

        if (_orderIds.Count > 0 || _itemIds.Count > 0)
        {
            var foundSuppliers = await repository.FindSupplierIdsByOrdersOrItemsAsync(_orderIds, _itemIds, cancellationToken);
            foreach (var s in foundSuppliers)
            {
                if (s != Guid.Empty)
                {
                    _suppliers.Add(s);
                }
            }
        }
    }

    // Đọc SupplierId tại lúc gọi (sau khi projector đã điền xong dòng item và resolve)
    public IReadOnlyCollection<Guid> SupplierIds =>
        _items.Select(item => item.SupplierId).Where(id => id != Guid.Empty).Concat(_suppliers).Distinct().ToArray();
}
