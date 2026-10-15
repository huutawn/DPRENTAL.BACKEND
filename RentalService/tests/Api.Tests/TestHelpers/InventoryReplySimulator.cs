using System.Text.Json;
using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.IServices.Integration;
using Dprental.RentalService.Application.IServices.Payments;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Consumers;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Services.Checkout;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Dprental.RentalService.Api.Tests.TestHelpers;

/// <summary>Một lệnh allocation Rental đã ghi vào Outbox cho InventoryService.</summary>
internal sealed record InventoryCommand(
    Guid OutboxMessageId,
    string CommandType,
    Guid RentalOrderId,
    Guid RentalItemId,
    string OperationKey,
    JsonElement Payload);

/// <summary>
/// Giả lập InventoryService trả lời các lệnh allocation Rental đã ghi vào Outbox: dựng sự kiện Kafka đúng hợp đồng
/// rồi cho <see cref="PaymentEventsDispatcher"/> thật nhận vào Inbox và xử lý, như consumer chạy thật.
/// </summary>
internal sealed class InventoryReplySimulator(
    Func<RentalServiceDbContext> newContext,
    TimeProvider clock,
    IPaymentRentalClient? payment = null,
    Func<RentalServiceDbContext, IOrderCodeGenerator>? orderCodes = null,
    IOrderLockManager? locks = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HashSet<Guid> _answered = [];
    private readonly IOrderLockManager _locks = locks ?? new FakeOrderLockManager();
    private readonly FakeOrderCodeGenerator _defaultOrderCodes = new();

    /// <summary>Các lệnh loại <paramref name="commandType"/> trong Outbox mà Inventory giả chưa trả lời, theo thứ tự ghi.</summary>
    public async Task<IReadOnlyList<InventoryCommand>> PendingCommandsAsync(string commandType, Guid? rentalOrderId = null)
    {
        await using RentalServiceDbContext context = newContext();
        List<Dprental.RentalService.Domain.Entities.RentalOutboxMessage> messages = await context.RentalOutboxMessages
            .Where(m => m.MessageType == commandType)
            .ToListAsync();

        HashSet<Guid> answered;
        lock (_answered)
        {
            answered = [.. _answered];
        }

        return messages
            .Where(m => !answered.Contains(m.Id))
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Sequence)
            .Select(m =>
            {
                JsonElement payload = JsonDocument.Parse(m.Payload).RootElement.GetProperty("payload").Clone();
                return new InventoryCommand(
                    m.Id,
                    m.MessageType,
                    payload.GetProperty("rentalOrderId").GetGuid(),
                    payload.GetProperty("rentalItemId").GetGuid(),
                    payload.GetProperty("operationKey").GetString()!,
                    payload);
            })
            .Where(c => rentalOrderId is null || c.RentalOrderId == rentalOrderId)
            .ToList();
    }

    /// <summary>Inventory thực hiện xong lệnh. Với CreateHold, <paramref name="allocations"/> là các máy được gán.</summary>
    public Task<Result> SucceedAsync(InventoryCommand command, IReadOnlyList<AllocationAssetMappingPayload>? allocations = null) =>
        ReplyAsync(command, KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded,
            new InventoryAllocationCommandSucceededPayload(
                command.RentalOrderId, command.RentalItemId, command.OperationKey, command.CommandType,
                "Succeeded", Allocations: allocations, OccurredAt: clock.GetUtcNow()));

    public Task<Result> RejectAsync(InventoryCommand command, string errorCode, string errorMessage) =>
        ReplyAsync(command, KafkaContractSpec.EventTypes.InventoryAllocationCommandRejected,
            new InventoryAllocationCommandRejectedPayload(
                command.RentalOrderId, command.RentalItemId, command.OperationKey, command.CommandType,
                errorCode, errorMessage, clock.GetUtcNow()));

    /// <summary>Trả lời thành công mọi lệnh loại <paramref name="commandType"/> đang chờ.</summary>
    public async Task SucceedAllAsync(string commandType)
    {
        foreach (InventoryCommand command in await PendingCommandsAsync(commandType))
        {
            Result result = await SucceedAsync(command);
            if (result.IsFailure)
            {
                throw new InvalidOperationException($"Xử lý câu trả lời {commandType} thất bại: {result.Error.Code}");
            }
        }
    }

    /// <summary>Gửi lại đúng một câu trả lời đã gửi trước đó, với messageId mới như Inventory phát lại.</summary>
    public Task<Result> RedeliverSuccessAsync(InventoryCommand command) =>
        ReplyAsync(command, KafkaContractSpec.EventTypes.InventoryAllocationCommandSucceeded,
            new InventoryAllocationCommandSucceededPayload(
                command.RentalOrderId, command.RentalItemId, command.OperationKey, command.CommandType,
                "Succeeded", OccurredAt: clock.GetUtcNow()));

    /// <summary>Consumer xử lý lại các message trong Inbox chưa xong, như lượt quét sau của worker.</summary>
    public async Task<Result> ReprocessPendingInboxAsync()
    {
        List<Guid> pending;
        await using (RentalServiceDbContext context = newContext())
        {
            pending = await context.RentalInboxMessages
                .Where(m => m.Status != Dprental.RentalService.Domain.Entities.RentalInboxMessage.StatusProcessed &&
                            m.Status != Dprental.RentalService.Domain.Entities.RentalInboxMessage.StatusDeadLettered)
                .Select(m => m.Id)
                .ToListAsync();
        }

        foreach (Guid inboxId in pending)
        {
            await using RentalServiceDbContext context = newContext();
            Result result = await CreateDispatcher(context).ProcessInboxMessageAsync(inboxId);
            if (result.IsFailure)
            {
                return result;
            }
        }

        return Result.Success();
    }

    private async Task<Result> ReplyAsync<TPayload>(InventoryCommand command, string eventType, TPayload payload)
    {
        lock (_answered)
        {
            _answered.Add(command.OutboxMessageId);
        }
        KafkaMessageEnvelope<TPayload> envelope = new(
            Guid.NewGuid(),
            eventType,
            KafkaContractSpec.CurrentEnvelopeVersion,
            KafkaContractSpec.Producers.InventoryService,
            command.RentalOrderId.ToString("D"),
            clock.GetUtcNow(),
            command.RentalOrderId.ToString("D"),
            command.OutboxMessageId,
            payload);

        await using RentalServiceDbContext context = newContext();
        PaymentEventsDispatcher dispatcher = CreateDispatcher(context);

        Result<KafkaIntakeDisposition> received = await dispatcher.ReceiveMessageAsync(
            JsonSerializer.Serialize(envelope, JsonOptions), KafkaContractSpec.Topics.InventoryAllocationEvents);
        if (received.IsFailure)
        {
            return received.Error;
        }

        Guid inboxId = (await new InboxRepository(context).GetByMessageIdAsync(envelope.MessageId))!.Id;
        return await dispatcher.ProcessInboxMessageAsync(inboxId);
    }

    private PaymentEventsDispatcher CreateDispatcher(RentalServiceDbContext context)
    {
        RentalOrderRepository orders = new(context);
        OutboxRepository outbox = new(context);
        var options = Microsoft.Extensions.Options.Options.Create(new KafkaOptions { Enabled = true });

        return new PaymentEventsDispatcher(
            new InboxRepository(context),
            orders,
            _locks,
            Mock.Of<IPaymentWorkflowService>(),
            Mock.Of<IRefundCompletedConsumer>(),
            new KafkaOutboxWriter(outbox),
            context,
            new RentalCheckoutOperationRepository(context),
            new InventoryCommandProducer(outbox, options, clock),
            new CheckoutOrderWriter(
                orders,
                new RentalCartRepository(context),
                orderCodes?.Invoke(context) ?? _defaultOrderCodes,
                NullLogger<CheckoutOrderWriter>.Instance),
            new CheckoutInvoiceIssuer(
                payment ?? new FakePaymentRentalClient(),
                orders,
                context,
                clock,
                NullLogger<CheckoutInvoiceIssuer>.Instance),
            options,
            NullLogger<PaymentEventsDispatcher>.Instance,
            clock);
    }
}
