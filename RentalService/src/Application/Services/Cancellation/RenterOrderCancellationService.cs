using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.IServices.Cancellation;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Services.Cancellation;

/// <summary>
/// Lớp mỏng: xác định danh tính renter qua <see cref="RentalAuthorizationPolicy"/> rồi giao cho executor.
/// Kiểm tra sở hữu đơn, khóa order, idempotency và toàn bộ quy trình hủy nằm trong
/// <see cref="IRentalOperationExecutor.ExecuteRenterCancelOrderAsync"/>.
/// </summary>
public sealed class RenterOrderCancellationService(IRentalOperationExecutor operationExecutor)
    : IRenterOrderCancellationService
{
    // Xác thực người thuê và chuyển quyết định hủy cả order cho executor, nơi sở hữu khóa, replay và side effect.
    public async Task<Result<CancelOrderResponse>> CancelOrderAsync(
        Guid orderId,
        CancelOrderRequest? request,
        string idempotencyKey,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        // Kiểm tra danh tính/quyền của caller trước khi trả dữ liệu hoặc áp dụng quyết định trong phạm vi tài nguyên.
        Result<Guid> renter = RentalAuthorizationPolicy.AuthorizeRenterCaller(caller);
        // Trả lỗi của bước vừa thực hiện; không chạy phần xử lý phía sau khi bước tiên quyết chưa thành công.
        if (renter.IsFailure)
        {
            return renter.Error;
        }

        return await operationExecutor.ExecuteRenterCancelOrderAsync(
            orderId,
            renter.Value,
            request?.Reason,
            idempotencyKey,
            cancellationToken);
    }
}
