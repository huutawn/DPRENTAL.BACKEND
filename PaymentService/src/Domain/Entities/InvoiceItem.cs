using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Utils;

namespace Dprental.PaymentService.Domain.Entities;

[Table("invoice_items")]
public sealed class InvoiceItem
{
    [Key]
    public Guid Id { get; private set; }

    [Required]
    public Guid InvoiceId { get; private set; }

    [Required]
    public Guid RentalItemId { get; private set; }

    [Required]
    public Guid SupplierId { get; private set; }

    [Required]
    [Column(TypeName = "numeric(18,0)")]
    public decimal RentalAmount { get; private set; }

    [Required]
    [Column(TypeName = "numeric(18,0)")]
    public decimal DepositAmount { get; private set; }

    [Required]
    [Column(TypeName = "numeric(18,0)")]
    public decimal TotalAmount { get; private set; }

    [ForeignKey(nameof(InvoiceId))]
    public Invoice? Invoice { get; private set; }
    private InvoiceItem()
    {
    }

    public static Result<InvoiceItem> Create(
        Guid rentalItemId,
        Guid supplierId,
        decimal rentalAmount,
        decimal depositAmount)
    {
        if (rentalItemId == Guid.Empty || supplierId == Guid.Empty)
        {
            return Error.Validation("Payment.InvalidId", "RentalItemId và SupplierId phải là GUID hợp lệ.");
        }

        var rentalCheck = VndAmountValidator.ValidateVndAmount(rentalAmount, "Tiền thuê");
        if (rentalCheck.IsFailure) return rentalCheck.Error;

        var depositCheck = VndAmountValidator.ValidateVndAmount(depositAmount, "Tiền cọc");
        if (depositCheck.IsFailure) return depositCheck.Error;

        decimal total = rentalAmount + depositAmount;
        var totalCheck = VndAmountValidator.ValidateVndAmount(total, "Tổng tiền item");
        if (totalCheck.IsFailure) return totalCheck.Error;

        return Result.Success(new InvoiceItem
        {
            Id = Guid.NewGuid(),
            RentalItemId = rentalItemId,
            SupplierId = supplierId,
            RentalAmount = rentalAmount,
            DepositAmount = depositAmount,
            TotalAmount = total
        });
    }
}
