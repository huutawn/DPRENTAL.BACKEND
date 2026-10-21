using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Entities;

/// <summary>
/// Một địa chỉ giao trong sổ địa chỉ của người thuê. Checkout chép địa chỉ vào đơn,
/// nên sửa hoặc xóa địa chỉ ở đây không làm đổi các đơn đã đặt.
/// </summary>
[Table("renter_addresses", Schema = "rental")]
[Index(nameof(RenterId))]
public class RenterAddress
{
    [Key]
    public Guid Id { get; private set; }

    public Guid RenterId { get; private set; }

    [Required]
    [MaxLength(100)]
    public string Province { get; private set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string AddressDetail { get; private set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string RecipientName { get; private set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string RecipientPhone { get; private set; } = string.Empty;

    public bool IsDefault { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    private RenterAddress()
    {
    }

    public RenterAddress(
        Guid id,
        Guid renterId,
        string province,
        string addressDetail,
        string recipientName,
        string recipientPhone,
        bool isDefault,
        DateTimeOffset utcNow)
    {
        Id = id;
        RenterId = renterId;
        Province = province;
        AddressDetail = addressDetail;
        RecipientName = recipientName;
        RecipientPhone = recipientPhone;
        IsDefault = isDefault;
        CreatedAt = utcNow;
        UpdatedAt = utcNow;
    }

    public void Update(string province, string addressDetail, string recipientName, string recipientPhone, DateTimeOffset utcNow)
    {
        Province = province;
        AddressDetail = addressDetail;
        RecipientName = recipientName;
        RecipientPhone = recipientPhone;
        UpdatedAt = utcNow;
    }

    public void SetDefault(bool isDefault, DateTimeOffset utcNow)
    {
        if (IsDefault == isDefault) return;
        IsDefault = isDefault;
        UpdatedAt = utcNow;
    }
}
