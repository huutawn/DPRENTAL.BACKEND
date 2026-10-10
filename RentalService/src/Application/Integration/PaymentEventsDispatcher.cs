#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Integration;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Payments;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Integration;

public sealed class PaymentEventsDispatcher : IPaymentEventsDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IPaymentWorkflowService _paymentWorkflowService;
    private readonly IRefundCompletedConsumer _refundCompletedConsumer;
    private readonly IOutboxRepository _outboxRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<PaymentEventsDispatcher> _logger;
    private readonly string _rentalEventsTopic;

    public PaymentEventsDispatcher(
        IPaymentWorkflowService paymentWorkflowService,
        IRefundCompletedConsumer refundCompletedConsumer,
        IOutboxRepository outboxRepository,
        IUnitOfWork unitOfWork,
        IConfiguration configuration,
        ILogger<PaymentEventsDispatcher> logger)
    {
        _paymentWorkflowService = paymentWorkflowService ?? throw new ArgumentNullException(nameof(paymentWorkflowService));
        _refundCompletedConsumer = refundCompletedConsumer ?? throw new ArgumentNullException(nameof(refundCompletedConsumer));
        _outboxRepository = outboxRepository ?? throw new ArgumentNullException(nameof(outboxRepository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _rentalEventsTopic = configuration["Kafka:RentalEventsTopic"] ?? "rental.events.v1";
    }
    public async Task<Result> DispatchRawMessageAsync(string messageJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageJson))
        {
            return Error.Validation("EMPTY_MESSAGE", "Message không được rỗng.");
        }

        try
        {
            using var doc = JsonDocument.Parse(messageJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("messageType", out var msgTypeProp))
            {
                await RouteToDlqAsync(messageJson, "MISSING_MESSAGE_TYPE", "Thiếu trường messageType", cancellationToken);
                return Error.Validation("MISSING_MESSAGE_TYPE", "Thiếu trường messageType.");
            }

            string messageType = msgTypeProp.GetString() ?? string.Empty;

            if (string.Equals(messageType, PaymentContractSpec.EventTypes.InvoicePaid, StringComparison.OrdinalIgnoreCase))
            {
                var envelope = JsonSerializer.Deserialize<PaymentEventEnvelope<InvoicePaidPayload>>(messageJson, JsonOptions);
                if (envelope == null)
                {
                    await RouteToDlqAsync(messageJson, "DESERIALIZATION_FAILED", "Không thể deserialize InvoicePaid", cancellationToken);
                    return Error.Validation("DESERIALIZATION_FAILED", "Không thể deserialize InvoicePaid.");
                }

                return await _paymentWorkflowService.ProcessInvoicePaidAsync(envelope, cancellationToken);
            }
            else if (string.Equals(messageType, PaymentContractSpec.EventTypes.RefundCompleted, StringComparison.OrdinalIgnoreCase))
            {
                var envelope = JsonSerializer.Deserialize<PaymentEventEnvelope<RefundCompletedPayload>>(messageJson, JsonOptions);
                if (envelope == null)
                {
                    await RouteToDlqAsync(messageJson, "DESERIALIZATION_FAILED", "Không thể deserialize RefundCompleted", cancellationToken);
                    return Error.Validation("DESERIALIZATION_FAILED", "Không thể deserialize RefundCompleted.");
                }

                return await _refundCompletedConsumer.ProcessRefundCompletedAsync(envelope, cancellationToken);
            }
            else
            {
                _logger.LogInformation("Ignored payment event with unhandled messageType: {Type}", messageType);
                return Result.Success();
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON parse failure on payment event");
            await RouteToDlqAsync(messageJson, "MALFORMED_JSON", ex.Message, cancellationToken);
            return Error.Validation("MALFORMED_JSON", "Cấu trúc JSON không hợp lệ.");
        }
    }

    private async Task RouteToDlqAsync(string rawMessage, string errorCode, string errorDetail, CancellationToken ct)
    {
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        Guid dlqId = Guid.NewGuid();

        var dlqEnvelope = new RentalEventEnvelope<DeadLetterPayload>(
            dlqId,
            RentalEventTypes.DeadLetter,
            1,
            "rental-service",
            "system",
            utcNow,
            null,
            null,
            new DeadLetterPayload("PaymentEvent", rawMessage, $"{errorCode}: {errorDetail}", utcNow));

        var outbox = new RentalOutboxMessage(
            dlqId,
            _rentalEventsTopic,
            "dlq",
            RentalEventTypes.DeadLetter,
            JsonSerializer.Serialize(dlqEnvelope, JsonOptions),
            utcNow);
        await _outboxRepository.AddAsync(outbox, ct);
        await _unitOfWork.SaveChangesAsync(ct);
        _logger.LogWarning("Routed invalid payment event to DLQ: {ErrorCode} - {Detail}", errorCode, errorDetail);
    }
}
