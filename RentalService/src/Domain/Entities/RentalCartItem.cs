using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Entities;

[Table("rental_cart_items", Schema = "rental")]
[Index(nameof(RentalCartId), nameof(EquipmentListingId), nameof(ExpectedStartDate), nameof(ExpectedEndDate), IsUnique = true)]
public class RentalCartItem
{
    [Key]
    public Guid Id { get; private set; }

    public Guid RentalCartId { get; private set; }

    public Guid EquipmentListingId { get; private set; }

    public Guid? EquipmentTypeId { get; private set; }

    public int Quantity { get; private set; }

    public DateOnly ExpectedStartDate { get; private set; }

    public DateOnly ExpectedEndDate { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    [ForeignKey(nameof(RentalCartId))]
    public RentalCart? RentalCart { get; private set; }

    // Constructor for EF Core
    private RentalCartItem()
    {
    }

    internal RentalCartItem(
        Guid id,
        Guid rentalCartId,
        Guid equipmentListingId,
        Guid? equipmentTypeId,
        int quantity,
        DateOnly expectedStartDate,
        DateOnly expectedEndDate,
        DateTimeOffset utcNow)
    {
        Id = id;
        RentalCartId = rentalCartId;
        EquipmentListingId = equipmentListingId;
        EquipmentTypeId = equipmentTypeId;
        Quantity = quantity;
        ExpectedStartDate = expectedStartDate;
        ExpectedEndDate = expectedEndDate;
        CreatedAt = utcNow;
        UpdatedAt = utcNow;
    }

    internal void UpdateQuantity(int quantity, DateTimeOffset utcNow)
    {
        Quantity = quantity;
        UpdatedAt = utcNow;
    }

    internal void UpdateDates(DateOnly expectedStartDate, DateOnly expectedEndDate, DateTimeOffset utcNow)
    {
        ExpectedStartDate = expectedStartDate;
        ExpectedEndDate = expectedEndDate;
        UpdatedAt = utcNow;
    }

    internal void AddQuantity(int delta, DateTimeOffset utcNow)
    {
        Quantity += delta;
        UpdatedAt = utcNow;
    }

    public void SetEquipmentTypeId(Guid equipmentTypeId)
    {
        if (equipmentTypeId == Guid.Empty) throw new ArgumentException("EquipmentTypeId cannot be empty.", nameof(equipmentTypeId));
        EquipmentTypeId = equipmentTypeId;
    }
}
