using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Utils;

namespace Dprental.PaymentService.Domain.Entities;

public sealed class InvoiceItem
{
    public Guid Id { get; private set; }
    public Guid InvoiceId { get; private set; }
    public Guid RentalItemId { get; private set; }
    public Guid SupplierId { get; private set; }
    public decimal RentalAmount { get; private set; }
    public decimal DepositAmount { get; private set; }
    public decimal TotalAmount { get; private set; }
    public long Revision { get; private set; } = 1;
    public Invoice? Invoice { get; private set; }
    internal void AttachToInvoice(Guid invoiceId)
    {
        InvoiceId = invoiceId;
    }

    private InvoiceItem() { }

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
