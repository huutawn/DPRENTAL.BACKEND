#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Operations;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.SupplierDecision;

public sealed class SupplierDecisionService : ISupplierDecisionService
{
    private readonly IRentalOrderRepository _orderRepository;
    private readonly IRentalOperationExecutor _operationExecutor;
    private readonly ILogger<SupplierDecisionService> _logger;

    public SupplierDecisionService(
        IRentalOrderRepository orderRepository,
        IRentalOperationExecutor operationExecutor,
        ILogger<SupplierDecisionService> logger)
    {
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _operationExecutor = operationExecutor ?? throw new ArgumentNullException(nameof(operationExecutor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Result<SupplierDecisionResponse>> AcceptItemAsync(
        Guid itemId,
        string idempotencyKey,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        var keyValidation = ValidateIdempotencyKey(idempotencyKey);
        if (keyValidation.IsFailure) return keyValidation.Error;

        var callerValidation = ValidateSupplierCaller(caller);
        if (callerValidation.IsFailure) return callerValidation.Error;

        Guid supplierId = caller.UserId!.Value;

        var order = await _orderRepository.GetByItemIdWithDetailsAsync(itemId, cancellationToken);
        if (order == null)
        {
            return Error.NotFound("ITEM_NOT_FOUND", $"Không tìm thấy mục thuê '{itemId}'.");
        }

        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null)
        {
            return Error.NotFound("ITEM_NOT_FOUND", $"Không tìm thấy mục thuê '{itemId}'.");
        }

        // IDOR check: Admin or service token cannot act on behalf of supplier
        if (item.SupplierId != supplierId)
        {
            return Error.Forbidden("SUPPLIER_MISMATCH", "Chỉ chủ sở hữu thiết bị mới có quyền chấp nhận yêu cầu thuê này.");
        }

        return await _operationExecutor.ExecuteSupplierAcceptAsync(
            order.Id,
            itemId,
            supplierId,
            idempotencyKey.Trim(),
            cancellationToken);
    }

    public async Task<Result<SupplierDecisionResponse>> RejectItemAsync(
        Guid itemId,
        SupplierRejectRequest request,
        string idempotencyKey,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        var keyValidation = ValidateIdempotencyKey(idempotencyKey);
        if (keyValidation.IsFailure) return keyValidation.Error;

        var callerValidation = ValidateSupplierCaller(caller);
        if (callerValidation.IsFailure) return callerValidation.Error;

        if (request == null || string.IsNullOrWhiteSpace(request.RejectReason) || request.RejectReason.Trim().Length > 500)
        {
            return Error.Validation("INVALID_REJECT_REASON", "Lý do từ chối là bắt buộc và tối đa 500 ký tự.");
        }

        if (request.OptionalNote != null && request.OptionalNote.Trim().Length > 2000)
        {
            return Error.Validation("INVALID_OPTIONAL_NOTE", "Ghi chú không được vượt quá 2000 ký tự.");
        }

        Guid supplierId = caller.UserId!.Value;

        var order = await _orderRepository.GetByItemIdWithDetailsAsync(itemId, cancellationToken);
        if (order == null)
        {
            return Error.NotFound("ITEM_NOT_FOUND", $"Không tìm thấy mục thuê '{itemId}'.");
        }

        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null)
        {
            return Error.NotFound("ITEM_NOT_FOUND", $"Không tìm thấy mục thuê '{itemId}'.");
        }

        if (item.SupplierId != supplierId)
        {
            return Error.Forbidden("SUPPLIER_MISMATCH", "Chỉ chủ sở hữu thiết bị mới có quyền từ chối yêu cầu thuê này.");
        }

        return await _operationExecutor.ExecuteSupplierRejectAsync(
            order.Id,
            itemId,
            supplierId,
            request.RejectReason.Trim(),
            string.IsNullOrWhiteSpace(request.OptionalNote) ? null : request.OptionalNote.Trim(),
            idempotencyKey.Trim(),
            cancellationToken);
    }

    private static Result ValidateIdempotencyKey(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 64)
        {
            return Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key là bắt buộc và không được vượt quá 64 ký tự.");
        }
        return Result.Success();
    }

    private static Result ValidateSupplierCaller(IRentalCallerContext caller)
    {
        if (!caller.IsAuthenticated || caller.IsService || !caller.UserId.HasValue || caller.UserId.Value == Guid.Empty)
        {
            return Error.Unauthorized("AUTH_REQUIRED", "Yêu cầu đăng nhập tài khoản cá nhân của supplier.");
        }
        return Result.Success();
    }
}
