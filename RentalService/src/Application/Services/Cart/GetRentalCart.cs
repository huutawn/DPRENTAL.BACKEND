using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Services.Cart;

/// <remarks>
/// Luồng đọc giỏ: nạp giỏ hiện tại → đánh giá lại từng dòng bằng RentalCartItemEvaluator → dựng read model.
/// Giá và khả dụng phản ánh dữ liệu Inventory tại lần đọc; checkout vẫn phải kiểm tra lại.
/// </remarks>
public sealed class GetRentalCart(
    IRentalCartRepository carts,
    RentalCartItemEvaluator evaluator)
{
    // Đọc giỏ đang hoạt động và đánh giá lại từng dòng theo Inventory; giỏ không giữ giá hay giữ lịch thay cho checkout.
    public async Task<Result<RentalCartReadModel>> ExecuteAsync(
        Guid renterId,
        CancellationToken cancellationToken = default)
    {
        // Từ chối định danh rỗng trước khi tra cứu hoặc gắn dữ liệu vào tài nguyên của luồng.
        if (renterId == Guid.Empty)
        {
            return Result.Success(new RentalCartReadModel(Guid.Empty, []));
        }

        // Đọc dữ liệu/định danh từ repository để xác định đối tượng và trạng thái cần xử lý; kiểm tra không tồn tại ở nhánh kế tiếp.
        RentalCart? cart = await carts.GetActiveByRenterIdAsync(renterId, cancellationToken);
        // Chọn nhánh theo trạng thái đã ghi nhận; không áp lại quyết định của luồng này cho tài nguyên ở trạng thái khác.
        if (cart is null || cart.Status != RentalCartStatus.Active)
        {
            return Result.Success(new RentalCartReadModel(cart?.Id ?? Guid.Empty, []));
        }

        // Tích lũy response theo các phần tử đã kiểm tra; danh sách này chỉ phục vụ kết quả của lần xử lý hiện tại.
        var lines = new List<RentalCartLineReadModel>();

        // Xử lý từng dòng trong phạm vi đã chọn; lỗi/kết quả của dòng được ghi theo đúng ID tương ứng.
        foreach (RentalCartItem item in cart.Items)
        {
            // Chuyển bước xử lý sang use case/core đang sở hữu trách nhiệm này và kiểm tra kết quả trước khi đi tiếp.
            Result<RentalCartItemEvaluationResult> evalResult = await evaluator.EvaluateItemAsync(
                renterId,
                item.EquipmentListingId,
                item.Quantity,
                item.ExpectedStartDate,
                item.ExpectedEndDate,
                cancellationToken);

            // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
            if (evalResult.IsFailure)
            {
                return evalResult.Error;
            }

            // Dùng policy/mapper hiện có để dựng dữ liệu response nhất quán với các điểm đọc khác của cùng nghiệp vụ.
            RentalCartLineReadModel line = evaluator.ToLineReadModel(
                item.Id,
                item.EquipmentListingId,
                item.Quantity,
                item.ExpectedStartDate,
                item.ExpectedEndDate,
                evalResult.Value);

            // Tích lũy response theo các phần tử đã kiểm tra; danh sách này chỉ phục vụ kết quả của lần xử lý hiện tại.
            lines.Add(line);
        }

        return Result.Success(new RentalCartReadModel(cart.Id, lines));
    }
}
