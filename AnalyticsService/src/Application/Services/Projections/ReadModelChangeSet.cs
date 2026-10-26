using Dprental.AnalyticsService.Domain.Entities;

namespace Dprental.AnalyticsService.Application.Services.Projections;

/// <summary>
/// Ghi lại các dòng item projector đã chạm tới trong lúc xử lý MỘT message (scoped).
/// Sau khi lưu DB thành công, dùng để biết cần báo SignalR cho NCC nào.
/// </summary>
public sealed class ReadModelChangeSet
{
    private readonly List<RentalItemFact> _items = [];

    public void TrackItem(RentalItemFact item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
    }

    // Đọc SupplierId tại thời điểm gọi (sau khi projector đã điền xong)
    public IReadOnlyCollection<Guid> SupplierIds =>
        _items.Where(item => item.SupplierId is not null).Select(item => item.SupplierId!.Value).Distinct().ToArray();
}
