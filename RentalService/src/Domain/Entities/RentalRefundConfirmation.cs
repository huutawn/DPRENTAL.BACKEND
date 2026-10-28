using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Domain.Entities;

/// <summary>
/// Admin xác nhận đã chuyển khoản hoàn tiền cho người thuê của một dòng thiết bị. Số tiền và tài khoản nhận được chép
/// lúc xác nhận để đối soát; mỗi dòng thiết bị chỉ có một xác nhận và mỗi mã giao dịch ngân hàng chỉ dùng một lần.
/// </summary>
[Table("rental_refund_confirmations", Schema = "rental")]
public class RentalRefundConfirmation
{
    [Key]
    public Guid Id { get; private set; }

    public Guid RentalOrderId { get; private set; }

    public RentalOrder? RentalOrder { get; private set; }

    public Guid RentalItemId { get; private set; }

    public RentalRefundKind Kind { get; private set; }

    public decimal RefundAmount { get; private set; }

    [MaxLength(50)]
    public string? BankCode { get; private set; }

    [MaxLength(20)]
    public string? AccountNumber { get; private set; }

    [MaxLength(100)]
    public string? AccountHolderName { get; private set; }

    [Required]
    [MaxLength(64)]
    public string BankTransactionRef { get; private set; } = string.Empty;

    [MaxLength(500)]
    public string? Note { get; private set; }

    public Guid ConfirmedBy { get; private set; }

    public DateTimeOffset ConfirmedAt { get; private set; }

    private RentalRefundConfirmation()
    {
    }

    public RentalRefundConfirmation(
        Guid id,
        Guid rentalOrderId,
        Guid rentalItemId,
        RentalRefundKind kind,
        decimal refundAmount,
        string? bankCode,
        string? accountNumber,
        string? accountHolderName,
        string bankTransactionRef,
        string? note,
        Guid confirmedBy,
        DateTimeOffset confirmedAt)
    {
        Id = id;
        RentalOrderId = rentalOrderId;
        RentalItemId = rentalItemId;
        Kind = kind;
        RefundAmount = refundAmount;
        BankCode = bankCode;
        AccountNumber = accountNumber;
        AccountHolderName = accountHolderName;
        BankTransactionRef = bankTransactionRef;
        Note = note;
        ConfirmedBy = confirmedBy;
        ConfirmedAt = confirmedAt;
    }
}
