#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Operations;

public sealed partial class RentalOperationExecutor
{
    private async Task<Result<SupplierDecisionResponse>> ConfirmAllocationCoreAsync(
        RentalOrder order,
        RentalItem item,
        RentalOperationReceipt receipt,
        string operationKey,
        CancellationToken cancellationToken)
    {
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();

        // Đã hoàn tất thành công trước đó (idempotent replay)
        if (receipt.IsSuccess && receipt.Status == RentalOperationReceipt.StatusSucceeded)
        {
            if (!string.IsNullOrWhiteSpace(receipt.ApiResponseJson))
            {
                try
                {
                    var resp = JsonSerializer.Deserialize<SupplierDecisionResponse>(receipt.ApiResponseJson, JsonOptions);
                    if (resp != null) return resp;
                }
                catch { }
            }
            return BuildSupplierResponse(order, item);
        }

        // Kiểm tra ngân sách chờ 10 giây cho cuộc gọi HTTP
        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        callCts.CancelAfter(TimeSpan.FromSeconds(10));

        // HTTP retries, scheduled recovery and admin reconciliation share the persisted provider key.
        operationKey = receipt.InventoryOperationKey ?? ReadConfirmProviderKey(receipt) ?? operationKey;
        receipt.SetInventoryOperationKey(operationKey);
        receipt.BeginProviderAttempt(utcNow);
        await CommitLocalChangesAsync(order, cancellationToken);

        var confirmReq = new ConfirmAllocationRequest(operationKey);
        Result<ConfirmAllocationResponse> httpResult;
        try
        {
            httpResult = await _inventoryClient.ConfirmAllocationAsync(item.AllocationGroupId, confirmReq, callCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            httpResult = Error.ServiceUnavailable("Inventory.Timeout", "InventoryService không phản hồi kịp thời (timeout 10s).");
        }
        catch (Exception ex)
        {
            httpResult = Error.ServiceUnavailable("Inventory.Unavailable", ex.Message);
        }

        DateTimeOffset afterCallNow = _timeProvider.GetUtcNow();

        // Tái nạp dữ liệu dưới lock để đảm bảo tính nhất quán (nếu có cuộc gọi song song / webhook)
        var freshOrder = await _orderRepository.GetByIdWithDetailsAsync(order.Id, cancellationToken) ?? order;
        var freshItem = freshOrder.Items.FirstOrDefault(i => i.Id == item.Id) ?? item;
        var freshReceipt = freshOrder.OperationReceipts.FirstOrDefault(r => r.OperationId == receipt.OperationId) ?? receipt;

        if (freshReceipt.IsSuccess && freshReceipt.Status == RentalOperationReceipt.StatusSucceeded)
        {
            if (!string.IsNullOrWhiteSpace(freshReceipt.ApiResponseJson))
            {
                try
                {
                    var resp = JsonSerializer.Deserialize<SupplierDecisionResponse>(freshReceipt.ApiResponseJson, JsonOptions);
                    if (resp != null) return resp;
                }
                catch { }
            }
            return BuildSupplierResponse(freshOrder, freshItem);
        }

        if (httpResult.IsSuccess)
        {
            var validationResult = ValidateAllocationResponse(freshItem, httpResult.Value);
            if (validationResult.IsFailure)
            {
                freshReceipt.MarkFailed(validationResult.Error.Code, validationResult.Error.Message, afterCallNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
                await CommitLocalChangesAsync(freshOrder, cancellationToken);
                return validationResult.Error;
            }

            var applyResult = await ApplyConfirmAllocationResultAsync(
                freshOrder, freshReceipt, freshItem.Id, operationKey, isSuccess: true, httpResult.Value, null, null, afterCallNow, cancellationToken);
            if (applyResult.IsFailure) return applyResult.Error;

            return BuildSupplierResponse(freshOrder, freshItem);
        }
        else
        {
            var error = httpResult.Error;
            bool isUncertain = error.Type == ErrorType.ServiceUnavailable ||
                               error.Code.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
                               error.Code.Contains("Unavailable", StringComparison.OrdinalIgnoreCase);

            if (isUncertain)
            {
                freshReceipt.MarkUncertain(error.Code, error.Message, afterCallNow);
                await CommitLocalChangesAsync(freshOrder, cancellationToken);
                return Error.ServiceUnavailable(error.Code, error.Message);
            }
            else
            {
                freshReceipt.MarkFailed(error.Code, error.Message, afterCallNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
                await CommitLocalChangesAsync(freshOrder, cancellationToken);
                return error;
            }
        }
    }

    public async Task<Result> ApplyConfirmAllocationResultAsync(
        RentalOrder order,
        RentalOperationReceipt receipt,
        Guid rentalItemId,
        string operationKey,
        bool isSuccess,
        ConfirmAllocationResponse? confirmData,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            if (isSuccess)
            {
                var applied = await ApplySuccessfulConfirmationAsync(order, receipt, rentalItemId,
                    operationKey, confirmData, utcNow, cancellationToken);
                if (applied.IsFailure)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    _unitOfWork.DiscardChanges();
                    return applied.Error;
                }
            }
            else
            {
                receipt.MarkFailed(errorCode ?? "COMMAND_REJECTED", errorMessage ?? "Rejected by Inventory", utcNow, RentalOperationReceipt.ClassificationBusiness, isTransient: false);
            }

            await CommitLocalChangesAsync(order, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result.Success();
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _unitOfWork.DiscardChanges();
            throw;
        }
    }

    private async Task<Result> ApplySuccessfulConfirmationAsync(
        RentalOrder order,
        RentalOperationReceipt receipt,
        Guid rentalItemId,
        string operationKey,
        ConfirmAllocationResponse? confirmData,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        string? apiResponseJson = null;
        var item = order.Items.FirstOrDefault(i => i.Id == rentalItemId);
        if (item != null)
        {
            if (item.Status == RentalItemStatus.PendingSupplierConfirmation)
            {
                item.ApplySupplierAccept(item.SupplierId, utcNow, confirmData?.ReceiptRef ?? operationKey, operationKey);
                RentalOrderStatusPolicy.Apply(order, utcNow, operationKey);
                order.RecordReadModelUpdate(utcNow);
                apiResponseJson = SerializeSupplierResponse(order, item);

                var outboxPayload = new RentalItemConfirmedPayload(
                    order.Id,
                    item.Id,
                    item.SupplierId,
                    utcNow,
                    confirmData?.ReceiptRef ?? operationKey,
                    order.Version);

                var published = await _rentalEvents.EnqueueRentalItemConfirmedAsync(
                    outboxPayload,
                    order.Id.ToString("D"),
                    causationId: receipt.Id,
                    cancellationToken: cancellationToken);
                if (published.IsFailure) return published.Error;
            }
            else
            {
                _logger.LogWarning("ConfirmAllocation succeeded late for item {ItemId} in status {Status}; not restoring", item.Id, item.Status);
                apiResponseJson = SerializeSupplierResponse(order, item);
            }

            string rawPayload = confirmData != null ? JsonSerializer.Serialize(confirmData, JsonOptions) : "{}";
            receipt.MarkSucceeded(utcNow, rawPayload, apiResponseJson);
        }
        return Result.Success();
    }

    private static Result ValidateAllocationResponse(RentalItem item, ConfirmAllocationResponse confirmData)
    {
        if (string.IsNullOrWhiteSpace(confirmData.ReceiptRef))
        {
            return Error.Unprocessable("INVALID_ALLOCATION", "ReceiptRef từ Inventory không được rỗng.");
        }

        if (!string.Equals(confirmData.AllocationGroupId, item.AllocationGroupId, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Unprocessable("INVALID_ALLOCATION", "AllocationGroupId từ Inventory không khớp với đơn hàng.");
        }

        if (confirmData.Units == null || confirmData.Units.Count != item.UnitAssignments.Count)
        {
            return Error.Unprocessable("INVALID_ALLOCATION", "Số lượng units xác nhận không khớp với số lượng của đơn hàng.");
        }

        // All units must be distinct and confirmed
        var confirmedUnitIds = confirmData.Units.Select(u => u.AllocationId).ToList();
        if (confirmedUnitIds.Distinct().Count() != confirmedUnitIds.Count)
        {
            return Error.Unprocessable("INVALID_ALLOCATION", "Danh sách units từ Inventory có chứa AllocationId trùng lặp.");
        }

        foreach (var unit in item.UnitAssignments)
        {
            var match = confirmData.Units.FirstOrDefault(u => u.AllocationId == unit.AllocationId && u.AssetId == unit.AssetId);
            if (match == null)
            {
                return Error.Unprocessable("INVALID_ALLOCATION", $"Unit assignment {unit.AllocationId} không tồn tại trong kết quả từ Inventory.");
            }

            if (!string.Equals(match.Status, "Confirmed", StringComparison.OrdinalIgnoreCase))
            {
                return Error.Unprocessable("INVALID_ALLOCATION", $"Trạng thái của unit {unit.AllocationId} không phải là Confirmed (nhận: {match.Status}).");
            }
        }

        return Result.Success();
    }

}
