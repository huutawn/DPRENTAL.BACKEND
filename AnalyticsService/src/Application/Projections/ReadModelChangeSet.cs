using Dprental.AnalyticsService.Domain.Entities;

namespace Dprental.AnalyticsService.Application.Projections;

/// <summary>
/// Ghi lại những gì projector đã chạm tới khi xử lý MỘT message (scoped). Sau khi lưu DB thành công,
/// dùng để biết cần báo SignalR cho NCC nào.
/// </summary>
public sealed class ReadModelChangeSet
{
    private readonly List<RentalItemFact> _items = [];
    private readonly HashSet<Guid> _suppliers = [];

    public void TrackItem(RentalItemFact item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
    }

    public void TrackSupplier(Guid? supplierId)
    {
        if (supplierId is { } id && id != Guid.Empty)
        {
            _suppliers.Add(id);
        }
    }

    // Đọc SupplierId tại lúc gọi (sau khi projector đã điền xong dòng item)
    public IReadOnlyCollection<Guid> SupplierIds =>
        _items.Select(item => item.SupplierId).Where(id => id != Guid.Empty).Concat(_suppliers).Distinct().ToArray();
}
