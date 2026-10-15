using System.Security.Cryptography;
using System.Text.Json;
using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Kafka.Outbox;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.InventoryService.Application.Services;

public delegate AllocationCommandOutcome RejectionOutcomeFactory(
    string errorCode,
    string errorMessage,
    DateTimeOffset occurredAt);

public sealed class AllocationCommandCoordinator
{
    private const string RentalSourceSystem = "rental";

    private readonly IInventoryWriteTransaction _writeTransaction;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AllocationCommandCoordinator>? _logger;

    public AllocationCommandCoordinator(
        IInventoryWriteTransaction writeTransaction,
        TimeProvider? timeProvider = null,
        ILogger<AllocationCommandCoordinator>? logger = null)
    {
        _writeTransaction = writeTransaction ?? throw new ArgumentNullException(nameof(writeTransaction));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public Task<Result<AllocationCommandOutcome>> CoordinateAsync<TPayload>(
        string commandType,
        Guid sourceMessageId,
        string? correlationId,
        Guid rentalOrderId,
        Guid rentalItemId,
        string operationKey,
        object businessPayloadToHash,
        string handlerName,
        Func<IInventoryWriteContext, DateTimeOffset, CancellationToken, Task<Result<AllocationCommandOutcome>>> useCaseAction,
        CancellationToken cancellationToken)
        => CoordinateAsync<TPayload>(
            commandType,
            sourceMessageId,
            correlationId,
            rentalOrderId,
            rentalItemId,
            operationKey,
            businessPayloadToHash,
            handlerName,
            useCaseAction,
            rejectionFactory: null,
            cancellationToken: cancellationToken);

    public async Task<Result<AllocationCommandOutcome>> CoordinateAsync<TPayload>(
        string commandType,
        Guid sourceMessageId,
        string? correlationId,
        Guid rentalOrderId,
        Guid rentalItemId,
        string operationKey,
        object businessPayloadToHash,
        string handlerName,
        Func<IInventoryWriteContext, DateTimeOffset, CancellationToken, Task<Result<AllocationCommandOutcome>>> useCaseAction,
        RejectionOutcomeFactory? rejectionFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandType);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);

        var groupRef = $"rental-item:{rentalItemId}";
        var fingerprint = ComputeFingerprint(commandType, businessPayloadToHash);
        var effectiveCorrelationId = string.IsNullOrWhiteSpace(correlationId) ? rentalOrderId.ToString("D") : correlationId;

        rejectionFactory ??= (errCode, errMsg, occurredAt) =>
            AllocationCommandOutcome.Rejected(commandType, operationKey, rentalOrderId, rentalItemId, errCode, errMsg, occurredAt);

        return await _writeTransaction.ExecuteAllocationCommandAsync(RentalSourceSystem, operationKey, groupRef, async (ctx, ct) =>
        {
            var nowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());

            // 1. Kiểm tra Idempotent Replay bằng receipt
            var existingReceipt = await ctx.GetReceiptAsync(RentalSourceSystem, operationKey, ct);
            if (existingReceipt is not null)
            {
                // Nhánh mismatch fingerprint: chỉ phát rejected reply, KHÔNG ghi đè receipt và KHÔNG add receipt trùng key
                if (existingReceipt.PayloadFingerprint != fingerprint)
                {
                    _logger?.LogWarning("Idempotency key reused with different fingerprint for {OpKey}", operationKey);
                    var mismatchOutcome = rejectionFactory("IDEMPOTENCY_KEY_REUSED", "Operation key đã được dùng cho thao tác hoặc payload khác.", nowUtc);
                    StageRejectionReply(ctx, sourceMessageId, effectiveCorrelationId, rentalOrderId, mismatchOutcome, nowUtc);
                    return Result.Success(mismatchOutcome);
                }

                // Replay đúng fingerprint: stage lại đúng kết quả đã lưu (Succeeded hoặc Rejected)
                if (string.IsNullOrWhiteSpace(existingReceipt.ResultPayloadJson))
                {
                    return Error.Failure("CORRUPTED_RECEIPT_PAYLOAD", "Receipt không chứa kết quả hợp lệ để replay.");
                }

                if (existingReceipt.Status == AllocationReceiptStatus.Succeeded)
                {
                    if (string.Equals(commandType, AllocationMessageTypes.ApplyReturnedUnits, StringComparison.OrdinalIgnoreCase))
                    {
                        var applied = JsonSerializer.Deserialize<ReturnedUnitsAppliedPayload>(existingReceipt.ResultPayloadJson);
                        if (applied is null) return Error.Failure("CORRUPTED_RECEIPT_PAYLOAD", "Payload return đã lưu bị hỏng.");
                        var outboxReply = InventoryAllocationOutboxEvents.CreateReturnedUnitsAppliedOutboxMessage(
                            rentalOrderId, applied, effectiveCorrelationId, sourceMessageId, nowUtc);
                        ctx.StageOutbox(outboxReply);
                        return Result.Success(AllocationCommandOutcome.ReturnApplied(operationKey, rentalOrderId, rentalItemId, applied));
                    }
                    else
                    {
                        var succeeded = JsonSerializer.Deserialize<InventoryAllocationCommandSucceededPayload>(existingReceipt.ResultPayloadJson);
                        if (succeeded is null) return Error.Failure("CORRUPTED_RECEIPT_PAYLOAD", "Payload succeeded đã lưu bị hỏng.");
                        var outboxReply = InventoryAllocationOutboxEvents.CreateSucceededOutboxMessage(
                            rentalOrderId, succeeded, effectiveCorrelationId, sourceMessageId, nowUtc);
                        ctx.StageOutbox(outboxReply);
                        return Result.Success(AllocationCommandOutcome.Succeeded(commandType, operationKey, rentalOrderId, rentalItemId, succeeded));
                    }
                }
                else
                {
                    // Replay rejection từ ResultPayloadJson
                    if (string.Equals(commandType, AllocationMessageTypes.ApplyReturnedUnits, StringComparison.OrdinalIgnoreCase))
                    {
                        var retRejected = JsonSerializer.Deserialize<ReturnedUnitsRejectedPayload>(existingReceipt.ResultPayloadJson);
                        if (retRejected is null) return Error.Failure("CORRUPTED_RECEIPT_PAYLOAD", "Payload rejection đã lưu bị hỏng.");
                        var outboxReply = InventoryAllocationOutboxEvents.CreateReturnedUnitsRejectedOutboxMessage(
                            rentalOrderId, retRejected, effectiveCorrelationId, sourceMessageId, nowUtc);
                        ctx.StageOutbox(outboxReply);
                        return Result.Success(AllocationCommandOutcome.ReturnRejected(
                            operationKey, rentalOrderId, rentalItemId, retRejected.AllocationGroupId, retRejected.ReturnRecordId, retRejected.ErrorCode, retRejected.ErrorMessage));
                    }
                    else
                    {
                        var cmdRejected = JsonSerializer.Deserialize<InventoryAllocationCommandRejectedPayload>(existingReceipt.ResultPayloadJson);
                        if (cmdRejected is null) return Error.Failure("CORRUPTED_RECEIPT_PAYLOAD", "Payload rejection đã lưu bị hỏng.");
                        var outboxReply = InventoryAllocationOutboxEvents.CreateRejectedOutboxMessage(
                            rentalOrderId, cmdRejected, effectiveCorrelationId, sourceMessageId, nowUtc);
                        ctx.StageOutbox(outboxReply);
                        return Result.Success(AllocationCommandOutcome.Rejected(
                            commandType, operationKey, rentalOrderId, rentalItemId, cmdRejected.ErrorCode, cmdRejected.ErrorMessage, nowUtc));
                    }
                }
            }

            // 2. Kiểm tra Group Tombstone (Release trước Hold)
            var tombstone = await ctx.GetGroupTombstoneAsync(RentalSourceSystem, groupRef, ct);
            if (tombstone is not null && string.Equals(commandType, AllocationMessageTypes.CreateHold, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogWarning("Hold arrived after group {GroupRef} was closed", groupRef);
                var closedOutcome = rejectionFactory("GROUP_ALREADY_RELEASED", "Nhóm đã bị đóng bởi lệnh release trước đó; không thể tạo giữ chỗ mới.", nowUtc);
                SaveRejectionReceiptAndStageReply(
                    ctx, sourceMessageId, effectiveCorrelationId, rentalOrderId, operationKey, groupRef, commandType, fingerprint, closedOutcome, nowUtc);
                return Result.Success(closedOutcome);
            }

            // 3. Thực thi use case nghiệp vụ
            Result<AllocationCommandOutcome> outcomeResult;
            try
            {
                outcomeResult = await useCaseAction(ctx, nowUtc, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Lỗi hạ tầng khi thực thi use case cho {CommandType} {OpKey}", commandType, operationKey);
                throw;
            }

            // 4. Nếu use case thành công -> Lưu Succeeded receipt + Outbox
            if (outcomeResult.IsSuccess && outcomeResult.Value.IsSuccess)
            {
                var outcome = outcomeResult.Value;
                bool closesGroup = string.Equals(commandType, AllocationMessageTypes.ReleaseAllocation, StringComparison.OrdinalIgnoreCase);

                if (string.Equals(commandType, AllocationMessageTypes.ApplyReturnedUnits, StringComparison.OrdinalIgnoreCase))
                {
                    var applied = outcome.ReturnedUnitsAppliedPayload!;
                    var resultJson = JsonSerializer.Serialize(applied);

                    var receipt = AllocationOperationReceipt.CreateSucceeded(
                        id: Guid.Parse(applied.ReceiptRef),
                        sourceSystem: RentalSourceSystem,
                        operationKey: operationKey,
                        rentalOrderId: rentalOrderId,
                        groupReference: groupRef,
                        commandType: commandType,
                        payloadFingerprint: fingerprint,
                        closesGroup: false,
                        resultPayloadJson: resultJson,
                        createdAt: nowUtc);

                    ctx.AddReceipt(receipt);

                    var outboxReply = InventoryAllocationOutboxEvents.CreateReturnedUnitsAppliedOutboxMessage(
                        rentalOrderId, applied, effectiveCorrelationId, sourceMessageId, nowUtc);

                    ctx.StageOutbox(outboxReply);
                    return outcomeResult;
                }
                else
                {
                    var successPayload = outcome.SuccessPayload!;
                    var resultJson = JsonSerializer.Serialize(successPayload);

                    var receipt = AllocationOperationReceipt.CreateSucceeded(
                        id: Guid.NewGuid(),
                        sourceSystem: RentalSourceSystem,
                        operationKey: operationKey,
                        rentalOrderId: rentalOrderId,
                        groupReference: groupRef,
                        commandType: commandType,
                        payloadFingerprint: fingerprint,
                        closesGroup: closesGroup,
                        resultPayloadJson: resultJson,
                        createdAt: nowUtc);

                    ctx.AddReceipt(receipt);

                    var outboxReply = InventoryAllocationOutboxEvents.CreateSucceededOutboxMessage(
                        rentalOrderId, successPayload, effectiveCorrelationId, sourceMessageId, nowUtc);

                    ctx.StageOutbox(outboxReply);
                    return outcomeResult;
                }
            }

            // 5. Nếu use case trả lỗi nghiệp vụ
            var error = outcomeResult.IsFailure ? outcomeResult.Error : Error.Failure("BUSINESS_REJECTION", "Thao tác bị từ chối.");

            await ctx.RollbackToSavepointAsync("sp_domain_mutation", ct);
            ctx.ClearChangeTracker();
            await ctx.MarkInboxProcessedAsync(handlerName, sourceMessageId.ToString("D"), nowUtc, ct);

            var rejectionOutcome = rejectionFactory(error.Code, error.Message, nowUtc);
            SaveRejectionReceiptAndStageReply(
                ctx, sourceMessageId, effectiveCorrelationId, rentalOrderId, operationKey, groupRef, commandType, fingerprint, rejectionOutcome, nowUtc);

            return Result.Success(rejectionOutcome);
        }, cancellationToken);
    }

    private static void SaveRejectionReceiptAndStageReply(
        IInventoryWriteContext ctx,
        Guid sourceMessageId,
        string correlationId,
        Guid rentalOrderId,
        string operationKey,
        string groupRef,
        string commandType,
        string fingerprint,
        AllocationCommandOutcome outcome,
        DateTimeOffset nowUtc)
    {
        string errorCode;
        string errorMessage;
        string? payloadJson;

        if (outcome.ReturnedUnitsRejectedPayload != null)
        {
            errorCode = outcome.ReturnedUnitsRejectedPayload.ErrorCode;
            errorMessage = outcome.ReturnedUnitsRejectedPayload.ErrorMessage;
            payloadJson = JsonSerializer.Serialize(outcome.ReturnedUnitsRejectedPayload);
        }
        else
        {
            errorCode = outcome.RejectionPayload?.ErrorCode ?? "REJECTED";
            errorMessage = outcome.RejectionPayload?.ErrorMessage ?? "Rejected";
            payloadJson = JsonSerializer.Serialize(outcome.RejectionPayload);
        }

        var rejectedReceipt = AllocationOperationReceipt.CreateRejected(
            id: Guid.NewGuid(),
            sourceSystem: RentalSourceSystem,
            operationKey: operationKey,
            rentalOrderId: rentalOrderId,
            groupReference: groupRef,
            commandType: commandType,
            payloadFingerprint: fingerprint,
            closesGroup: false,
            errorCode: errorCode,
            errorMessage: errorMessage,
            createdAt: nowUtc);

        // Lưu payload JSON vào receipt để replay bảo toàn nguyên vẹn
        typeof(AllocationOperationReceipt)
            .GetProperty(nameof(AllocationOperationReceipt.ResultPayloadJson))?
            .SetValue(rejectedReceipt, payloadJson);

        ctx.AddReceipt(rejectedReceipt);

        StageRejectionReply(ctx, sourceMessageId, correlationId, rentalOrderId, outcome, nowUtc);
    }

    private static void StageRejectionReply(
        IInventoryWriteContext ctx,
        Guid sourceMessageId,
        string correlationId,
        Guid rentalOrderId,
        AllocationCommandOutcome outcome,
        DateTimeOffset nowUtc)
    {
        if (outcome.ReturnedUnitsRejectedPayload != null)
        {
            var outboxReply = InventoryAllocationOutboxEvents.CreateReturnedUnitsRejectedOutboxMessage(
                rentalOrderId, outcome.ReturnedUnitsRejectedPayload, correlationId, sourceMessageId, nowUtc);
            ctx.StageOutbox(outboxReply);
        }
        else if (outcome.RejectionPayload != null)
        {
            var outboxReply = InventoryAllocationOutboxEvents.CreateRejectedOutboxMessage(
                rentalOrderId, outcome.RejectionPayload, correlationId, sourceMessageId, nowUtc);
            ctx.StageOutbox(outboxReply);
        }
    }

    private static string ComputeFingerprint(string commandType, object payload)
    {
        var wrapper = new { CommandType = commandType, Payload = payload };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(wrapper);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
