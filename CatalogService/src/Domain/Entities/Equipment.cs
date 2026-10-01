using Dprental.CatalogService.Common.Enums;

namespace Dprental.CatalogService.Domain.Entities;

/// <summary>
/// Một equipment listing thuộc sở hữu của một organization. Đây là aggregate chính của Catalog.
/// </summary>
public class Equipment
{
    /// <summary>
    /// Giá trị JSON mặc định khi equipment chưa khai báo thông số kỹ thuật.
    /// </summary>
    public const string EmptySpecifications = "{}";

    public Guid Id { get; set; }

    /// <summary>
    /// Organization sở hữu listing. Chỉ là tham chiếu logic tới Auth Service, không có FK.
    /// </summary>
    public Guid OrganizationId { get; set; }

    public Guid CategoryId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Mã thiết bị do organization tự đặt, duy nhất trong phạm vi organization.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    public string? Brand { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// Giá thuê theo ngày.
    /// </summary>
    public decimal DailyPrice { get; set; }

    /// <summary>
    /// Khu vực cho thuê. Chỉ là tham chiếu logic, chưa có bảng region trong Catalog.
    /// </summary>
    public Guid RegionId { get; set; }

    /// <summary>
    /// Trạng thái vòng đời, chỉ thay đổi qua <see cref="Publish"/> và <see cref="Hide"/>.
    /// </summary>
    public EquipmentStatus Status { get; private set; } = EquipmentStatus.Draft;

    /// <summary>
    /// Thông số kỹ thuật dạng JSON object, khác nhau giữa các loại equipment.
    /// </summary>
    public string Specifications { get; set; } = EmptySpecifications;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? UpdatedAtUtc { get; set; }

    /// <summary>
    /// Thời điểm publish gần nhất. Null khi listing chưa từng được publish.
    /// </summary>
    public DateTimeOffset? PublishedAtUtc { get; private set; }

    public Category? Category { get; set; }

    public ICollection<EquipmentMedia> Media { get; } = new List<EquipmentMedia>();

    /// <summary>
    /// Đưa listing lên public catalog từ trạng thái Draft hoặc Hidden.
    /// </summary>
    public void Publish(DateTimeOffset utcNow)
    {
        if (Status == EquipmentStatus.Published)
        {
            throw new InvalidOperationException("Equipment đang ở trạng thái Published.");
        }

        // PostgreSQL timestamptz chỉ nhận offset 0, nên chuẩn hóa về UTC trước khi lưu.
        utcNow = utcNow.ToUniversalTime();
        Status = EquipmentStatus.Published;
        PublishedAtUtc = utcNow;
        UpdatedAtUtc = utcNow;
    }

    /// <summary>
    /// Ẩn listing đang Published khỏi public catalog.
    /// </summary>
    public void Hide(DateTimeOffset utcNow)
    {
        if (Status != EquipmentStatus.Published)
        {
            throw new InvalidOperationException("Chỉ có thể ẩn equipment đang ở trạng thái Published.");
        }

        Status = EquipmentStatus.Hidden;
        UpdatedAtUtc = utcNow.ToUniversalTime();
    }
}
