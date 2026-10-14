#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Operations;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
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

    public async Task<Result<SubOrderDecisionResponse>> AcceptSubOrderAsync(
        Guid subOrderId,
        string idempotencyKey,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        var keyValidation = ValidateIdempotencyKey(idempotencyKey);
        if (keyValidation.IsFailure) return keyValidation.Error;

        var callerValidation = ValidateSupplierCaller(caller);
        if (callerValidation.IsFailure) return callerValidation.Error;

        Guid supplierId = caller.UserId!.Value;

        // Lookup only the sub-order before lock using AsNoTracking
        RentalSubOrder? subOrder = await _orderRepository.GetSubOrderByIdAsync(subOrderId, cancellationToken);
        if (subOrder is null || subOrder.SupplierId != supplierId)
        {
            return SubOrderNotFound(subOrderId);
        }

        var result = await _operationExecutor.ExecuteSubOrderAcceptAsync(
            subOrder.RentalOrderId,
            subOrder.Id,
            supplierId,
            idempotencyKey.Trim(),
            cancellationToken);

        return result.IsSuccess ? ToResponse(subOrder, result.Value) : result.Error;
    }

    public async Task<Result<SubOrderDecisionResponse>> RejectSubOrderAsync(
        Guid subOrderId,
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

        // Lookup only the sub-order before lock using AsNoTracking
        RentalSubOrder? subOrder = await _orderRepository.GetSubOrderByIdAsync(subOrderId, cancellationToken);
        if (subOrder is null || subOrder.SupplierId != supplierId)
        {
            return SubOrderNotFound(subOrderId);
        }

        var result = await _operationExecutor.ExecuteSubOrderRejectAsync(
            subOrder.RentalOrderId,
            subOrder.Id,
            supplierId,
            request.RejectReason.Trim(),
            string.IsNullOrWhiteSpace(request.OptionalNote) ? null : request.OptionalNote.Trim(),
            idempotencyKey.Trim(),
            cancellationToken);

        return result.IsSuccess ? ToResponse(subOrder, result.Value) : result.Error;
    }

    // Đơn con của supplier khác trả như không tồn tại để không lộ dữ liệu.
    private static Error SubOrderNotFound(Guid subOrderId) =>
        Error.NotFound("SUB_ORDER_NOT_FOUND", $"Không tìm thấy đơn con '{subOrderId}'.");

    private static SubOrderDecisionResponse ToResponse(RentalSubOrder subOrder, IReadOnlyList<SupplierDecisionResponse> items)
    {
        RentalItemStatus status = SubOrderStatusPolicy.Summarize(
            items.Select(i => Enum.Parse<RentalItemStatus>(i.Status)).ToList());
        return new SubOrderDecisionResponse(subOrder.Id, subOrder.SubOrderCode, subOrder.RentalOrderId, status.ToString(), items);
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
