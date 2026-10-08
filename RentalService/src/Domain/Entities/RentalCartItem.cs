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

    public int Quantity { get; private set; }

    public DateOnly ExpectedStartDate { get; private set; }

    public DateOnly ExpectedEndDate { get; private set; }

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
        int quantity,
        DateOnly expectedStartDate,
        DateOnly expectedEndDate)
    {
        Id = id;
        RentalCartId = rentalCartId;
        EquipmentListingId = equipmentListingId;
        Quantity = quantity;
        ExpectedStartDate = expectedStartDate;
        ExpectedEndDate = expectedEndDate;
    }

    internal void Update(int quantity, DateOnly expectedStartDate, DateOnly expectedEndDate)
    {
        Quantity = quantity;
        ExpectedStartDate = expectedStartDate;
        ExpectedEndDate = expectedEndDate;
    }

    internal void AddQuantity(int delta)
    {
        Quantity += delta;
    }
}
