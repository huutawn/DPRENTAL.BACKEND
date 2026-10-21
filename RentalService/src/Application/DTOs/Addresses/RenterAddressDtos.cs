namespace Dprental.RentalService.Application.DTOs.Addresses;

/// <summary>
/// Thông tin một địa chỉ giao khi thêm mới hoặc sửa. Luật giống ô <c>delivery</c> của checkout.
/// </summary>
/// <param name="Province">Tỉnh, thành phố (bắt buộc, tối đa 100 ký tự).</param>
/// <param name="AddressDetail">Địa chỉ chi tiết (bắt buộc, 5 đến 200 ký tự).</param>
/// <param name="RecipientName">Tên người nhận (bắt buộc, tối đa 100 ký tự).</param>
/// <param name="RecipientPhone">Số điện thoại người nhận: bắt đầu bằng 0 hoặc +84, theo sau 9 chữ số.</param>
public sealed record SaveRenterAddressRequest(
    string? Province,
    string? AddressDetail,
    string? RecipientName,
    string? RecipientPhone);

/// <summary>
/// Một địa chỉ trong sổ địa chỉ của người thuê.
/// </summary>
/// <param name="Id">Mã địa chỉ.</param>
/// <param name="Province">Tỉnh, thành phố.</param>
/// <param name="AddressDetail">Địa chỉ chi tiết.</param>
/// <param name="RecipientName">Tên người nhận.</param>
/// <param name="RecipientPhone">Số điện thoại người nhận.</param>
/// <param name="IsDefault">Địa chỉ mặc định, dùng để điền sẵn nơi giao lúc checkout.</param>
public sealed record RenterAddressResponse(
    Guid Id,
    string Province,
    string AddressDetail,
    string RecipientName,
    string RecipientPhone,
    bool IsDefault);
