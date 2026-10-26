using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Entities;

/// <summary>
/// Một tài khoản ngân hàng nhận hoàn tiền trong sổ tài khoản của người thuê. Checkout chép tài khoản vào đơn,
/// nên sửa hoặc xóa tài khoản ở đây không làm đổi các đơn đã đặt.
/// </summary>
[Table("renter_bank_accounts", Schema = "rental")]
[Index(nameof(RenterId), nameof(BankCode), nameof(AccountNumber), IsUnique = true)]
public class RenterBankAccount
{
    [Key]
    public Guid Id { get; private set; }

    public Guid RenterId { get; private set; }

    [Required]
    [MaxLength(50)]
    public string BankCode { get; private set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string AccountNumber { get; private set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string AccountHolderName { get; private set; } = string.Empty;

    public bool IsDefault { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    private RenterBankAccount()
    {
    }

    public RenterBankAccount(
        Guid id,
        Guid renterId,
        string bankCode,
        string accountNumber,
        string accountHolderName,
        bool isDefault,
        DateTimeOffset utcNow)
    {
        Id = id;
        RenterId = renterId;
        BankCode = bankCode;
        AccountNumber = accountNumber;
        AccountHolderName = accountHolderName;
        IsDefault = isDefault;
        CreatedAt = utcNow;
        UpdatedAt = utcNow;
    }

    public void Update(string bankCode, string accountNumber, string accountHolderName, DateTimeOffset utcNow)
    {
        BankCode = bankCode;
        AccountNumber = accountNumber;
        AccountHolderName = accountHolderName;
        UpdatedAt = utcNow;
    }

    public void SetDefault(bool isDefault, DateTimeOffset utcNow)
    {
        if (IsDefault == isDefault) return;
        IsDefault = isDefault;
        UpdatedAt = utcNow;
    }
}
