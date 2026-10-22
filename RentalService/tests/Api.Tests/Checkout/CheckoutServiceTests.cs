using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Services.Checkout;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Checkout;

public sealed class CheckoutServiceTests
{
    private static readonly DateTimeOffset Now = CheckoutTestHarness.Now;
    private const string Key = "checkout-key-1";
    private static readonly System.Text.Json.JsonSerializerOptions CamelCaseOptions = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
    private const string OtherKey = "checkout-key-2";
    // Fixture này dùng cọc = 0.
    // Máy ảnh: 2 máy x 500.000 x 3 ngày = 3.000.000. Ống kính: 1 cái x 200.000 x 3 ngày = 600.000.
    private const decimal CameraAndLensTotal = 3_600_000m;
    // Chân máy: 1 cái x 100.000 x 3 ngày = 300.000.
    private const decimal TripodTotal = 300_000m;

    private readonly CheckoutTestHarness _harness = new();
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _supplierId = Guid.NewGuid();
    private readonly Guid _cameraListingId;
    private readonly Guid _lensListingId;
    private readonly Guid _tripodListingId;
    private readonly Guid[] _cartItemIds;

    public CheckoutServiceTests()
    {
        _cameraListingId = _harness.AddListing(_supplierId, dailyRate: 500_000m);
        _lensListingId = _harness.AddListing(_supplierId, dailyRate: 200_000m);
        _tripodListingId = _harness.AddListing(_supplierId, dailyRate: 100_000m);

        _cartItemIds = _harness.SeedCartAsync(_renterId,
            (_cameraListingId, 2),
            (_lensListingId, 1),
            (_tripodListingId, 1)).GetAwaiter().GetResult();
    }

    private CheckoutRequest Request(decimal expectedTotal = CameraAndLensTotal, params Guid[] cartItemIds) =>
        CheckoutTestHarness.Request(expectedTotal, cartItemIds is { Length: > 0 } ? cartItemIds : [_cartItemIds[0], _cartItemIds[1]]);

    private CheckoutService CreateService(RentalServiceDbContext context, IOrderLockManager? locks = null)
    {
        RentalOrderRepository orders = new(context);
        RentalCartRepository carts = new(context);
        RentalCheckoutOperationRepository operations = new(context);
        OutboxRepository outbox = new(context);
        InventoryCommandProducer inventoryCommands = new(outbox, Options.Create(new KafkaOptions { Enabled = true }), _harness.Clock);
        CheckoutOrderWriter orderWriter = new(orders, carts, _harness.OrderCodes, context, NullLogger<CheckoutOrderWriter>.Instance);
        RentalEventProducer rentalEvents = new(new KafkaOutboxWriter(outbox), Options.Create(new KafkaOptions { Enabled = true }), _harness.Clock);
        CheckoutInvoiceIssuer invoiceIssuer = new(_harness.Payment, orders, operations, context, _harness.Clock, NullLogger<CheckoutInvoiceIssuer>.Instance);

        return new CheckoutService(
            new CheckoutPreparationService(
                carts,
                orders,
                new RenterAddressRepository(context),
                _harness.Listings,
                Options.Create(new RentalTermsOptions
                {
                    CurrentVersion = "v1.0",
                    DocumentRef = "terms/v1.0.pdf",
                    ContentHash = "hash123456"
                }),
                _harness.Clock),
            operations,
            orders,
            locks ?? _harness.Locks,
            _harness.Inventory,
            inventoryCommands,
            orderWriter,
            rentalEvents,
            invoiceIssuer,
            context,
            _harness.Clock,
            _harness.Payment);
    }

    [Fact]
    public async Task SubmitAsync_HoldsLines_AndReturnsOrderAndQrUrl()
    {
        await using var context = _harness.NewContext();
        var service = CreateService(context);

        var result = await service.SubmitAsync(new CheckoutRenterContext(_renterId, true), Request(), Key);

        Assert.True(result.IsSuccess);
        var resp = result.Value;
        Assert.NotEqual(Guid.Empty, resp.OrderId);
        Assert.StartsWith("DH-", resp.OrderCode);
        Assert.Equal("PendingPayment", resp.Status);
        Assert.NotNull(resp.Payment);
        Assert.NotEmpty(resp.Payment.QrUrl);
        Assert.Equal("Pending", resp.Payment.Status);

        var operations = new RentalCheckoutOperationRepository(context);
        string operationId = CheckoutRules.DeriveOperationId(_renterId, Key);
        var op = await operations.GetByOperationIdAsync(operationId);
        Assert.NotNull(op);
        Assert.Equal(CheckoutOperationStatus.Succeeded, op.Status);
    }

    [Fact]
    public async Task SubmitAsync_SendsInventoryTheLastRentalDayInclusive()
    {
        await using var context = _harness.NewContext();
        var service = CreateService(context);

        var result = await service.SubmitAsync(new CheckoutRenterContext(_renterId, true), Request(), Key);

        Assert.True(result.IsSuccess);
        // Giỏ thuê 12, 13, 14; đơn lưu [12, 15). Inventory nhận ngày cuối có tính: endDate = 14, không phải 15.
        Assert.All(_harness.Inventory.HoldRequests, r =>
        {
            Assert.Equal(new DateOnly(2026, 10, 12), r.StartDate);
            Assert.Equal(new DateOnly(2026, 10, 14), r.EndDate);
        });
    }

    [Fact]
    public async Task SubmitAsync_SameKeyAndPayload_ReturnsIdempotentAcceptedWithoutDuplicatingOutbox()
    {
        await using var context = _harness.NewContext();
        var service = CreateService(context);

        var first = await service.SubmitAsync(new CheckoutRenterContext(_renterId, true), Request(), Key);
        Assert.True(first.IsSuccess);

        var second = await service.SubmitAsync(new CheckoutRenterContext(_renterId, true), Request(), Key);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value.OrderId, second.Value.OrderId);
        Assert.Equal(first.Value.OrderCode, second.Value.OrderCode);
        Assert.True(second.Value.IsReplay);
        Assert.NotNull(second.Value.Payment);
        Assert.NotEmpty(second.Value.Payment.QrUrl);
    }

    [Fact]
    public async Task SubmitAsync_SameKeyDifferentPayload_ReturnsIdempotencyKeyReused()
    {
        await using var context = _harness.NewContext();
        var service = CreateService(context);

        var first = await service.SubmitAsync(new CheckoutRenterContext(_renterId, true), Request(), Key);
        Assert.True(first.IsSuccess);

        var second = await service.SubmitAsync(new CheckoutRenterContext(_renterId, true), Request(TripodTotal, _cartItemIds[2]), Key);
        Assert.True(second.IsFailure);
        Assert.Equal(CheckoutErrors.IdempotencyKeyReused, second.Error);
    }

    [Fact]
    public async Task SubmitAsync_WhenActiveOperationInProgress_ReturnsConflict()
    {
        await using var context = _harness.NewContext();
        var service = CreateService(context);
        string firstOpId = CheckoutRules.DeriveOperationId(_renterId, Key);
        var op = new RentalCheckoutOperation(
            firstOpId, _renterId, "fp", Guid.NewGuid(), Now.AddMinutes(30), "{}", "[]", Now);
        context.RentalCheckoutOperations.Add(op);
        await context.SaveChangesAsync();
        var second = await service.SubmitAsync(new CheckoutRenterContext(_renterId, true), Request(TripodTotal, _cartItemIds[2]), OtherKey);
        Assert.True(second.IsFailure);
        Assert.Equal("ACTIVE_CHECKOUT_IN_PROGRESS", second.Error.Code);
    }

    [Fact]
    public async Task SubmitAsync_WhenLockBusy_ReturnsInProgress()
    {
        await using var context = _harness.NewContext();
        var service = CreateService(context, locks: new BusyLockManager());

        var result = await service.SubmitAsync(new CheckoutRenterContext(_renterId, true), Request(), Key);

        Assert.True(result.IsFailure);
        Assert.Equal(CheckoutErrors.InProgress, result.Error);
    }

    [Fact]
    public async Task GetOperationStatusAsync_ReturnsProcessingStatus()
    {
        await using var context = _harness.NewContext();
        var service = CreateService(context);

        var submitResult = await service.SubmitAsync(new CheckoutRenterContext(_renterId, true), Request(), Key);
        Assert.True(submitResult.IsSuccess);

        string operationId = CheckoutRules.DeriveOperationId(_renterId, Key);
        var statusResult = await service.GetOperationStatusAsync(operationId, _renterId);
        Assert.True(statusResult.IsSuccess);
        Assert.Equal("Succeeded", statusResult.Value.Status);
        Assert.True(statusResult.Value.ReadyForPayment);
    }

    [Fact]
    public async Task GetOperationStatusAsync_OnlyReadyAfterInvoiceIsAttached()
    {
        string operationId = CheckoutRules.DeriveOperationId(_renterId, Key);
        Guid orderId;
        await using (var context = _harness.NewContext())
        {
            var submitted = await CreateService(context).SubmitAsync(
                new CheckoutRenterContext(_renterId, true), Request(), Key);
            Assert.True(submitted.IsSuccess);
            orderId = submitted.Value.OrderId;
        }

        await using (var context = _harness.NewContext())
        {
            var ready = await CreateService(context).GetOperationStatusAsync(operationId, _renterId);
            Assert.True(ready.IsSuccess);
            Assert.Equal("Ready", ready.Value.InvoiceStatus);
            Assert.True(ready.Value.ReadyForPayment);
        }

        _harness.Clock.UtcNow = Now.AddMinutes(30);
        await using (var context = _harness.NewContext())
        {
            var expiredWindow = await CreateService(context).GetOperationStatusAsync(operationId, _renterId);
            Assert.True(expiredWindow.IsSuccess);
            Assert.False(expiredWindow.Value.ReadyForPayment);
        }
    }

    [Fact]
    public async Task CreateInvoice_KeepsRetryingPastGenericBudgetUntilPaymentDeadline()
    {
        _harness.Payment.AlwaysFailWith = FakePaymentRentalClient.Down;
        string opId = CheckoutRules.DeriveOperationId(_renterId, Key);
        Guid orderId;
        await using (var context = _harness.NewContext())
        {
            var submitted = await CreateService(context).SubmitAsync(
                new CheckoutRenterContext(_renterId, true), Request(), Key);
            Assert.True(submitted.IsFailure);
            Assert.Equal(CheckoutErrors.CheckoutNotReady, submitted.Error);
            orderId = (await new RentalCheckoutOperationRepository(context)
                .GetByOperationIdAsync(opId))!.ExpectedRentalOrderId;
        }
        for (int attempt = 0; attempt < 11; attempt++)
        {
            Assert.True((await _harness.Completion.RunPendingInvoiceAsync(orderId)).IsFailure);
            _harness.Clock.UtcNow = _harness.Clock.UtcNow.AddMinutes(1).AddSeconds(1);
        }

        await using (var context = _harness.NewContext())
        {
            var receipt = await context.RentalOperationReceipts.SingleAsync(r =>
                r.RentalOrderId == orderId && r.Action == "CreateInvoice");
            Assert.True(receipt.RetryCount > 10);
            Assert.NotEqual(RentalOperationReceipt.StatusFailed, receipt.Status);
        }

        _harness.Payment.AlwaysFailWith = null;
        Assert.True((await _harness.Completion.RunPendingInvoiceAsync(orderId)).IsSuccess);
        await using (var context = _harness.NewContext())
        {
            var order = await context.RentalOrders.SingleAsync(o => o.Id == orderId);
            Assert.False(string.IsNullOrWhiteSpace(order.InvoiceRef));
        }
    }
    [Fact]
    public async Task GetOperationStatusAsync_ProcessingStatus_HasReadyForPaymentFalse()
    {
        await using var context = _harness.NewContext();
        var service = CreateService(context);

        string opId = CheckoutRules.DeriveOperationId(_renterId, Key);
        var op = new RentalCheckoutOperation(
            opId, _renterId, "fp", Guid.NewGuid(), Now.AddMinutes(30), "{}", "[]", Now);
        context.RentalCheckoutOperations.Add(op);
        await context.SaveChangesAsync();

        var statusResult = await service.GetOperationStatusAsync(opId, _renterId);
        Assert.True(statusResult.IsSuccess);
        Assert.Equal("Processing", statusResult.Value.Status);
        Assert.False(statusResult.Value.ReadyForPayment);

        string json = System.Text.Json.JsonSerializer.Serialize(statusResult.Value, CamelCaseOptions);
        Assert.Contains("\"readyForPayment\":false", json);
    }

    [Fact]
    public async Task GetOperationStatusAsync_DifferentRenter_ReturnsNotFound()
    {
        await using var context = _harness.NewContext();
        var service = CreateService(context);

        var submitResult = await service.SubmitAsync(new CheckoutRenterContext(_renterId, true), Request(), Key);
        Assert.True(submitResult.IsSuccess);

        string operationId = CheckoutRules.DeriveOperationId(_renterId, Key);
        var statusResult = await service.GetOperationStatusAsync(operationId, Guid.NewGuid());
        Assert.True(statusResult.IsFailure);
        Assert.Equal("CHECKOUT_OPERATION_NOT_FOUND", statusResult.Error.Code);
    }

    [Fact]
    public async Task GetOperationStatusAsync_Matrix_ValidatesReadinessAndNormalizedInvoiceStatus()
    {
        await using var context = _harness.NewContext();
        var service = CreateService(context);

        string opId = CheckoutRules.DeriveOperationId(_renterId, Key);
        Guid expectedOrderId = Guid.NewGuid();
        var op = new RentalCheckoutOperation(
            opId, _renterId, "fp", expectedOrderId, Now.AddMinutes(30), "{}", "[]", Now);
        context.RentalCheckoutOperations.Add(op);
        await context.SaveChangesAsync();
        // Case 1: Succeeded but Order not created yet in DB
        op.MarkSucceeded(Now, invoiceStatus: "Pending");
        await context.SaveChangesAsync();

        var resMissingOrder = await service.GetOperationStatusAsync(opId, _renterId);
        Assert.True(resMissingOrder.IsSuccess);
        Assert.Equal("Succeeded", resMissingOrder.Value.Status);
        Assert.Null(resMissingOrder.Value.Order);
        Assert.Null(resMissingOrder.Value.InvoiceStatus);
        Assert.False(resMissingOrder.Value.ReadyForPayment);

        var order = CheckoutTestOrders.CreatePending(_renterId, opId, Now);
        typeof(RentalOrder).GetProperty(nameof(RentalOrder.Id))!.SetValue(order, expectedOrderId);
        context.RentalOrders.Add(order);
        await context.SaveChangesAsync();

        // Case 2: Order exists, InvoiceRef is null -> Pending invoice, readyForPayment false
        var resPendingInvoice = await service.GetOperationStatusAsync(opId, _renterId);
        Assert.True(resPendingInvoice.IsSuccess);
        Assert.Equal("Succeeded", resPendingInvoice.Value.Status);
        Assert.Equal("Pending", resPendingInvoice.Value.InvoiceStatus);
        Assert.False(resPendingInvoice.Value.ReadyForPayment);
        Assert.Null(resPendingInvoice.Value.Order!.Payment);

        // Case 3: Order has valid InvoiceRef, PendingPayment, before deadline -> Ready, readyForPayment true
        order.AttachInvoice("INV-001", Now.AddMinutes(1));
        await context.SaveChangesAsync();

        // Even if op.InvoiceStatus was still "Pending" in DB, response returns Ready!
        var resReady = await service.GetOperationStatusAsync(opId, _renterId);
        Assert.True(resReady.IsSuccess);
        Assert.Equal("Succeeded", resReady.Value.Status);
        Assert.Equal("Ready", resReady.Value.InvoiceStatus);
        Assert.True(resReady.Value.ReadyForPayment);
        Assert.Equal("INV-001", resReady.Value.Order!.Payment!.InvoiceRef);

        string jsonReady = System.Text.Json.JsonSerializer.Serialize(resReady.Value, CamelCaseOptions);
        Assert.Contains("\"readyForPayment\":true", jsonReady);

        // Case 4: Reached deadline exactly -> readyForPayment false
        _harness.Clock.UtcNow = Now.AddMinutes(30);
        var resAtDeadline = await service.GetOperationStatusAsync(opId, _renterId);
        Assert.True(resAtDeadline.IsSuccess);
        Assert.False(resAtDeadline.Value.ReadyForPayment);

        // Case 5: Order Paid -> readyForPayment false
        _harness.Clock.UtcNow = Now.AddMinutes(5);
        order.MarkAsPaid("INV-001", "PAY-001", Now.AddMinutes(5), Now.AddMinutes(5));
        await context.SaveChangesAsync();

        var resPaid = await service.GetOperationStatusAsync(opId, _renterId);
        Assert.True(resPaid.IsSuccess);
        Assert.Equal("Ready", resPaid.Value.InvoiceStatus);
        Assert.False(resPaid.Value.ReadyForPayment);
    }

    private sealed class BusyLockManager : IOrderLockManager
    {
        public Task<IAsyncDisposable> AcquireOrderLockAsync(Guid orderId, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            throw new TimeoutException();

        public Task<IAsyncDisposable> AcquireRenterLockAsync(Guid renterUserId, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            throw new TimeoutException();
    }
}
