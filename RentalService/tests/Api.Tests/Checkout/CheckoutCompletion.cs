using System.Text.Json;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Services.Checkout;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dprental.RentalService.Api.Tests.Checkout;

/// <summary>
/// Chạy nốt phần bất đồng bộ của một lần checkout như consumer và worker thật: Inventory giả giữ máy theo
/// lệnh CreateHold (từ chối khi hết máy), dispatcher tạo đơn từ câu trả lời, lệnh nhả máy được áp vào Inventory giả,
/// rồi executor tạo hóa đơn. Trả kết quả cuối cùng dạng <see cref="CheckoutResult"/>.
/// </summary>
internal sealed class CheckoutCompletion
{
    private readonly Func<RentalServiceDbContext> _newContext;
    private readonly TimeProvider _clock;
    private readonly FakeInventoryRentalClient _inventory;
    private readonly IPaymentRentalClient _payment;
    private readonly IOrderLockManager _locks;
    // Inventory thật tự khóa theo Listing; Inventory giả không an toàn đa luồng nên các lượt chạy nối đuôi nhau.
    private readonly object _inventorySync = new();
    private Task _inventoryTail = Task.CompletedTask;

    public CheckoutCompletion(
        Func<RentalServiceDbContext> newContext,
        TimeProvider clock,
        FakeInventoryRentalClient inventory,
        IPaymentRentalClient payment,
        IOrderLockManager locks,
        Func<RentalServiceDbContext, IOrderCodeGenerator> orderCodes)
    {
        _newContext = newContext;
        _clock = clock;
        _inventory = inventory;
        _payment = payment;
        _locks = locks;
        Replies = new InventoryReplySimulator(newContext, clock, payment, orderCodes, locks);
    }

    public InventoryReplySimulator Replies { get; }

    public async Task<CheckoutResult> CompleteAsync(Guid renterId, string key, CancellationToken cancellationToken = default)
    {
        string operationId = CheckoutRules.DeriveOperationId(renterId, key);
        RentalCheckoutOperation? op = await LoadOperationAsync(operationId, cancellationToken);
        if (op is null)
        {
            return CheckoutResult.Failure(CheckoutErrors.OrderNotSaved);
        }

        bool finishedBefore = op.Status != CheckoutOperationStatus.Processing;
        await RunInventoryAsync(op.ExpectedRentalOrderId);

        op = (await LoadOperationAsync(operationId, cancellationToken))!;
        if (op.Status is CheckoutOperationStatus.Failed or CheckoutOperationStatus.TimedOut)
        {
            return CheckoutResult.Failure(Error.Conflict(
                op.ErrorCode ?? op.Status.ToString(), op.ErrorMessage ?? "Thao tác gửi đơn thất bại."));
        }

        if (op.Status != CheckoutOperationStatus.Succeeded)
        {
            return CheckoutResult.Failure(CheckoutErrors.InProgress);
        }

        Result invoice = await RunPendingInvoiceAsync(op.ExpectedRentalOrderId, cancellationToken);
        RentalOrder order = await LoadOrderAsync(op.ExpectedRentalOrderId, cancellationToken);
        if (invoice.IsFailure)
        {
            return CheckoutResult.OrderFailure(
                invoice.Error.Type == ErrorType.ServiceUnavailable
                    ? CheckoutErrors.InvoiceTemporarilyUnavailable
                    : CheckoutErrors.InvoiceRejected,
                order);
        }

        return CheckoutResult.Success(order, isReplay: finishedBefore);
    }

    /// <summary>Inventory giả trả lời các lệnh giữ lịch của đơn và áp mọi lệnh nhả máy đang chờ.</summary>
    public Task RunInventoryAsync(Guid rentalOrderId)
    {
        lock (_inventorySync)
        {
            _inventoryTail = _inventoryTail
                .ContinueWith(_ => RunInventoryCoreAsync(rentalOrderId), TaskScheduler.Default)
                .Unwrap();
            return _inventoryTail;
        }
    }

    private async Task RunInventoryCoreAsync(Guid rentalOrderId)
    {
        foreach (InventoryCommand hold in await Replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.CreateHold, rentalOrderId))
        {
            Result<CreateHoldResponse> held = await _inventory.CreateHoldAsync(new CreateHoldRequest(
                hold.Payload.GetProperty("equipmentListingId").GetGuid(),
                hold.Payload.GetProperty("quantity").GetInt32(),
                hold.Payload.GetProperty("startDate").Deserialize<DateOnly>(),
                hold.Payload.GetProperty("endDate").Deserialize<DateOnly>(),
                hold.Payload.GetProperty("holdExpiresAt").GetDateTimeOffset(),
                KafkaContractSpec.Producers.RentalService,
                GroupReference(hold.RentalItemId)));

            Result replied = held.IsSuccess
                ? await Replies.SucceedAsync(hold, held.Value.Allocations
                    .Select(a => new AllocationAssetMappingPayload(a.Id, a.AssetId, $"ASSET-{a.AssetId:N}"))
                    .ToList())
                : await Replies.RejectAsync(hold, held.Error.Code, held.Error.Message);
            ThrowIfFailed(replied, KafkaContractSpec.CommandTypes.CreateHold);
        }

        foreach (InventoryCommand release in await Replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.ReleaseAllocation))
        {
            await _inventory.ReleaseAllocationAsync(
                GroupReference(release.RentalItemId),
                new ReleaseAllocationRequest(release.OperationKey, release.Payload.GetProperty("reason").GetString()!));
            ThrowIfFailed(await Replies.SucceedAsync(release), KafkaContractSpec.CommandTypes.ReleaseAllocation);
        }
    }

    /// <summary>Nhóm giữ máy của một dòng: dispatcher lưu đúng chuỗi này làm AllocationGroupId của RentalItem.</summary>
    private static string GroupReference(Guid rentalItemId) => $"rental-item:{rentalItemId}";

    private static void ThrowIfFailed(Result result, string commandType)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Xử lý câu trả lời {commandType} thất bại: {result.Error.Code} {result.Error.Message}");
        }
    }

    /// <summary>Chạy phiếu CreateInvoice mà dispatcher đã xếp lịch, như OperationRecoveryWorker.</summary>
    public async Task<Result> RunPendingInvoiceAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        await using RentalServiceDbContext context = _newContext();
        OperationReceiptRepository receipts = new(context);
        OperationReceiptDescriptor? invoice = (await receipts.GetPendingOperationDescriptorsAsync(_clock.GetUtcNow(), 50, excludedActions: [], unlimitedAttemptActions: ["CreateInvoice"], cancellationToken: cancellationToken))
            .FirstOrDefault(d => d.RentalOrderId == orderId && d.Action == "CreateInvoice");
        if (invoice is null)
        {
            RentalOrder order = await LoadOrderAsync(orderId, cancellationToken);
            return order.InvoiceRef is not null
                ? Result.Success()
                : Error.ServiceUnavailable("INVOICE_PENDING", "Hóa đơn chưa được tạo.");
        }

        RentalOrderRepository orders = new(context);
        OutboxRepository outbox = new(context);
        RentalOperationExecutor executor = new(
            orders,
            receipts,
            outbox,
            _locks,
            _inventory,
            _payment,
            new InventoryCommandProducer(outbox, Microsoft.Extensions.Options.Options.Create(new KafkaOptions { Enabled = true }), _clock),
            context,
            new ConfigurationBuilder().Build(),
            NullLogger<RentalOperationExecutor>.Instance,
            new CheckoutInvoiceIssuer(_payment, orders, context, _clock, NullLogger<CheckoutInvoiceIssuer>.Instance, new RentalCheckoutOperationRepository(context)),
            new RentalCheckoutOperationRepository(context),
            _clock);

        return await executor.ExecuteScheduledOperationAsync(invoice, cancellationToken);
    }

    private async Task<RentalCheckoutOperation?> LoadOperationAsync(string operationId, CancellationToken cancellationToken)
    {
        await using RentalServiceDbContext context = _newContext();
        return await new RentalCheckoutOperationRepository(context).GetByOperationIdAsync(operationId, cancellationToken);
    }

    private async Task<RentalOrder> LoadOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await using RentalServiceDbContext context = _newContext();
        return await context.RentalOrders
            .Include(o => o.Items).ThenInclude(i => i.UnitAssignments)
            .Include(o => o.SubOrders)
            .SingleAsync(o => o.Id == orderId, cancellationToken);
    }
}
