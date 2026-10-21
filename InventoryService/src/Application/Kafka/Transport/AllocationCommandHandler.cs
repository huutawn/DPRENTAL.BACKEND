using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Microsoft.Extensions.Logging;

namespace Dprental.InventoryService.Application.Kafka.Transport;

public sealed class AllocationCommandHandler :
    IMessageHandler<CreateHoldPayload>,
    IMessageHandler<ExtendHoldPayload>,
    IMessageHandler<ConfirmAllocationPayload>,
    IMessageHandler<ReleaseAllocationPayload>,
    IMessageHandler<MarkAssetsHandedOverPayload>,
    IMessageHandler<MarkAssetsOverduePayload>,
    IMessageHandler<ApplyReturnedUnitsCommandPayload>
{
    public const string ConsumerGroupId = "inventory-allocation-command-consumer";
    public const string ExpectedProducer = "RentalService";

    private readonly IInventoryAllocationService _allocationService;
    private readonly ILogger<AllocationCommandHandler>? _logger;

    public AllocationCommandHandler(
        IInventoryAllocationService allocationService,
        ILogger<AllocationCommandHandler>? logger = null)
    {
        _allocationService = allocationService ?? throw new ArgumentNullException(nameof(allocationService));
        _logger = logger;
    }

    public async Task<Result> HandleAsync(MessageEnvelope<CreateHoldPayload> envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var validation = ValidateProducer(envelope);
        if (validation.IsFailure) return validation;

        var result = await _allocationService.ProcessCreateHoldCommandAsync(envelope, KafkaServiceCallerContext.Instance, $"{nameof(AllocationCommandHandler)}_{AllocationMessageTypes.CreateHold}", cancellationToken);
        return result.IsSuccess ? Result.Success() : result.Error;
    }

    public async Task<Result> HandleAsync(MessageEnvelope<ExtendHoldPayload> envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var validation = ValidateProducer(envelope);
        if (validation.IsFailure) return validation;

        var result = await _allocationService.ProcessExtendHoldCommandAsync(envelope, KafkaServiceCallerContext.Instance, $"{nameof(AllocationCommandHandler)}_{AllocationMessageTypes.ExtendHold}", cancellationToken);
        return result.IsSuccess ? Result.Success() : result.Error;
    }

    public async Task<Result> HandleAsync(MessageEnvelope<ConfirmAllocationPayload> envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var validation = ValidateProducer(envelope);
        if (validation.IsFailure) return validation;

        var result = await _allocationService.ProcessConfirmAllocationCommandAsync(envelope, KafkaServiceCallerContext.Instance, $"{nameof(AllocationCommandHandler)}_{AllocationMessageTypes.ConfirmAllocation}", cancellationToken);
        return result.IsSuccess ? Result.Success() : result.Error;
    }

    public async Task<Result> HandleAsync(MessageEnvelope<ReleaseAllocationPayload> envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var validation = ValidateProducer(envelope);
        if (validation.IsFailure) return validation;

        var result = await _allocationService.ProcessReleaseAllocationCommandAsync(envelope, KafkaServiceCallerContext.Instance, $"{nameof(AllocationCommandHandler)}_{AllocationMessageTypes.ReleaseAllocation}", cancellationToken);
        return result.IsSuccess ? Result.Success() : result.Error;
    }

    public async Task<Result> HandleAsync(MessageEnvelope<MarkAssetsHandedOverPayload> envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var validation = ValidateProducer(envelope);
        if (validation.IsFailure) return validation;

        var result = await _allocationService.ProcessMarkAssetsHandedOverCommandAsync(envelope, KafkaServiceCallerContext.Instance, $"{nameof(AllocationCommandHandler)}_{AllocationMessageTypes.MarkAssetsHandedOver}", cancellationToken);
        return result.IsSuccess ? Result.Success() : result.Error;
    }

    public async Task<Result> HandleAsync(MessageEnvelope<ApplyReturnedUnitsCommandPayload> envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var validation = ValidateProducer(envelope);
        if (validation.IsFailure) return validation;

        var result = await _allocationService.ProcessApplyReturnedUnitsCommandAsync(envelope, KafkaServiceCallerContext.Instance, $"{nameof(AllocationCommandHandler)}_{AllocationMessageTypes.ApplyReturnedUnits}", cancellationToken);
        return result.IsSuccess ? Result.Success() : result.Error;
    }

    public async Task<Result> HandleAsync(MessageEnvelope<MarkAssetsOverduePayload> envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var validation = ValidateProducer(envelope);
        if (validation.IsFailure) return validation;
        var result = await _allocationService.ProcessMarkAssetsOverdueCommandAsync(
            envelope, KafkaServiceCallerContext.Instance,
            $"{nameof(AllocationCommandHandler)}_{AllocationMessageTypes.MarkAssetsOverdue}", cancellationToken);
        return result.IsSuccess ? Result.Success() : result.Error;
    }

    private static Result ValidateProducer<T>(MessageEnvelope<T> envelope)
    {
        if (!string.Equals(envelope.Producer, ExpectedProducer, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("INVALID_PRODUCER", $"Chỉ chấp nhận lệnh phân bổ từ producer '{ExpectedProducer}'.");
        }
        return Result.Success();
    }
}
