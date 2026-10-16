using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Entities;

/// <summary>
/// Phần của một đơn hàng thuộc về một nhà cung cấp. Người thuê xem đơn cha (<see cref="RentalOrder"/>),
/// mỗi nhà cung cấp chỉ xem đơn con của mình. Trạng thái vẫn nằm trên từng <see cref="RentalItem"/>.
/// </summary>
[Table("rental_sub_orders", Schema = "rental")]
[Index(nameof(SubOrderCode), IsUnique = true)]
[Index(nameof(RentalOrderId), nameof(SupplierId), IsUnique = true)]
[Index(nameof(SupplierId), nameof(CreatedAt))]
public class RentalSubOrder
{
    public const int MaxSubOrderCodeLength = 80;

    [Key]
    public Guid Id { get; private set; }

    public Guid RentalOrderId { get; private set; }

    public Guid SupplierId { get; private set; }

    [Required]
    [MaxLength(MaxSubOrderCodeLength)]
    public string SubOrderCode { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    [ForeignKey(nameof(RentalOrderId))]
    public RentalOrder? RentalOrder { get; private set; }

    private readonly List<RentalItem> _items = [];
    public IReadOnlyCollection<RentalItem> Items => _items.AsReadOnly();

    private RentalSubOrder()
    {
    }

    internal RentalSubOrder(Guid id, Guid rentalOrderId, Guid supplierId, string subOrderCode, DateTimeOffset createdAt)
    {
        Id = id;
        RentalOrderId = rentalOrderId;
        SupplierId = supplierId;
        SubOrderCode = subOrderCode;
        CreatedAt = createdAt;
    }

    /// <summary>Mã đơn con = mã đơn cha + số thứ tự hai chữ số, ví dụ DH-2026-000001-01.</summary>
    public static string BuildCode(string orderCode, int sequence) => $"{orderCode}-{sequence:D2}";

    internal void AddItem(RentalItem item) => _items.Add(item);
}
