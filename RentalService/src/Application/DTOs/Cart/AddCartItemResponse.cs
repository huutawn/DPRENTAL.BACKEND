using System;

namespace Dprental.RentalService.Api.Contracts.Cart;

/// <summary>
/// DTO phản hồi sau khi thêm một mục thiết bị vào giỏ hàng thành công.
/// </summary>
/// <param name="CartItemId">Mã định danh duy nhất của mục trong giỏ hàng (CartItemId) vừa được tạo.</param>
public sealed record AddCartItemResponse(Guid CartItemId);
