namespace Dprental.RentalService.Application.DTOs.BankAccounts;

/// <summary>
/// Thông tin một tài khoản ngân hàng khi thêm mới hoặc sửa. Luật giống ô <c>refundAccount</c> của checkout.
/// </summary>
/// <param name="BankCode">Mã ngân hàng (bắt buộc, tối đa 50 ký tự).</param>
/// <param name="AccountNumber">Số tài khoản (bắt buộc, 6 đến 20 chữ số).</param>
/// <param name="AccountHolderName">Tên chủ tài khoản (bắt buộc, 2 đến 100 ký tự).</param>
public sealed record SaveRenterBankAccountRequest(
    string? BankCode,
    string? AccountNumber,
    string? AccountHolderName);

/// <summary>
/// Một tài khoản trong sổ tài khoản ngân hàng của người thuê.
/// </summary>
/// <param name="Id">Mã tài khoản, gửi vào <c>refundAccountId</c> lúc checkout.</param>
/// <param name="BankCode">Mã ngân hàng.</param>
/// <param name="AccountNumber">Số tài khoản.</param>
/// <param name="AccountHolderName">Tên chủ tài khoản.</param>
/// <param name="IsDefault">Tài khoản mặc định, dùng để chọn sẵn tài khoản nhận hoàn tiền lúc checkout.</param>
public sealed record RenterBankAccountResponse(
    Guid Id,
    string BankCode,
    string AccountNumber,
    string AccountHolderName,
    bool IsDefault);

/// <summary>
/// Tài khoản nhận tiền của nhà cung cấp, chỉ Admin được xem để chuyển tiền quyết toán.
/// </summary>
/// <param name="BankCode">Mã ngân hàng.</param>
/// <param name="AccountNumber">Số tài khoản đầy đủ.</param>
/// <param name="AccountHolderName">Tên chủ tài khoản.</param>
public sealed record AdminSupplierBankAccountResponse(
    string BankCode,
    string AccountNumber,
    string AccountHolderName);
