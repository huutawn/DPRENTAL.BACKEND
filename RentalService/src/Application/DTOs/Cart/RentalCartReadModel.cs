using System;
using System.Collections.Generic;

namespace Dprental.RentalService.Application.DTOs.Cart;



/// <summary>
/// DTO mô hình đọc toàn bộ giỏ hàng của người thuê (Renter).
/// </summary>
/// <param name="CartId">Mã định danh duy nhất của giỏ hàng.</param>
/// <param name="Items">Danh sách các mục thiết bị trong giỏ hàng kèm trạng thái kiểm tra tính khả dụng và giá.</param>
public sealed record RentalCartReadModel(
    Guid CartId,
    IReadOnlyList<RentalCartLineReadModel> Items);