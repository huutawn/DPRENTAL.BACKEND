using Dprental.PaymentService.Application.DTOs.Payout;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.IRepository;

namespace Dprental.PaymentService.Application.Services;

public sealed class SupplierPayoutService : ISupplierPayoutService
{
    private const int MaxOperationKey = 64;
    private const int MaxTransferReference = 256;
    private const int MaxDestinationRef = 256;
    private const int MaxReason = 500;

    private readonly ISupplierPayoutRepository _repository;

    public SupplierPayoutService(ISupplierPayoutRepository repository) => _repository = repository;

    public async Task<Result<CompleteSupplierPayoutResponse>> CompleteAsync(
        Guid payoutId, Guid adminId, CompleteSupplierPayoutRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Validate(payoutId, adminId, request.OperationKey,
                request.ExternalTransferReference, "ExternalTransferReference", MaxTransferReference,
                request.AuditReason) is { } invalid)
            return invalid;

        var result = await _repository.CompleteAsync(new CompleteSupplierPayoutCommand(
            payoutId, adminId, request.OperationKey.Trim(),
            request.ExternalTransferReference.Trim(), request.AuditReason.Trim()), ct);

        if (!result.IsSuccess) return result.Error;

        var w = result.Value;
        return Result.Success(new CompleteSupplierPayoutResponse(
            w.PayoutId, w.PayoutRef, w.Status, request.OperationKey.Trim(), w.ExternalTransferReference!,
            w.CompletedAt!.Value, w.CompletedBy!.Value, w.IsReplay));
    }

    public async Task<Result<AttachPayoutDestinationResponse>> AttachDestinationAsync(
        Guid payoutId, Guid adminId, AttachPayoutDestinationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Validate(payoutId, adminId, request.OperationKey,
                request.DestinationRef, "DestinationRef", MaxDestinationRef,
                request.AuditReason) is { } invalid)
            return invalid;
        if (request.Version < 1)
            return Error.Validation("Payment.PayoutDestinationVersionInvalid", "Version phải từ 1 trở lên.");

        var result = await _repository.AttachDestinationAsync(new AttachPayoutDestinationCommand(
            payoutId, adminId, request.OperationKey.Trim(),
            request.DestinationRef.Trim(), request.AuditReason.Trim(), request.Version), ct);

        if (!result.IsSuccess) return result.Error;

        var w = result.Value;
        return Result.Success(new AttachPayoutDestinationResponse(
            w.PayoutId, w.PayoutRef, w.Status, w.DestinationRef!, w.IsReplay));
    }

    private static Error? Validate(
        Guid payoutId, Guid adminId, string? operationKey,
        string? value, string valueName, int valueMax, string? reason)
    {
        if (payoutId == Guid.Empty)
            return Error.Validation("Payment.PayoutIdInvalid", "payoutId không hợp lệ.");
        if (adminId == Guid.Empty)
            return Error.Validation("Payment.AdminIdInvalid", "Không xác định được admin thực hiện.");
        if (!IsClean(operationKey, MaxOperationKey))
            return Error.Validation("Payment.OperationKeyInvalid", $"OperationKey bắt buộc, tối đa {MaxOperationKey} ký tự.");
        if (!IsClean(value, valueMax))
            return Error.Validation("Payment.PayoutFieldInvalid", $"{valueName} bắt buộc, tối đa {valueMax} ký tự.");
        if (!IsClean(reason, MaxReason))
            return Error.Validation("Payment.AuditReasonInvalid", $"AuditReason bắt buộc, tối đa {MaxReason} ký tự.");
        return null;
    }

    private static bool IsClean(string? s, int max) =>
        !string.IsNullOrWhiteSpace(s) && s.Trim().Length <= max && !s.Any(char.IsControl);
}
