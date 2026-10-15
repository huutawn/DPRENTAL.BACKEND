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
    private const string OtherKey = "checkout-key-2";
    // Inventory chưa có tiền cọc nên cọc = 0.
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

        return new CheckoutService(
            new CheckoutPreparationService(
                carts,
                orders,
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
            inventoryCommands,
            context,
            _harness.Clock);
    }

    [Fact]
    public async Task SubmitAsync_EnqueuesHolds_AndReturnsAccepted()
    {
        await using var context = _harness.NewContext();
        var service = CreateService(context);

        var result = await service.SubmitAsync(new CheckoutRenterContext(_renterId, true), Request(), Key);

        Assert.True(result.IsSuccess);
        var accepted = result.Value;
        Assert.Equal(CheckoutRules.DeriveOperationId(_renterId, Key), accepted.OperationId);
        Assert.Equal($"/api/v1/rental/checkout/operations/{accepted.OperationId}", accepted.StatusUrl);

        var operations = new RentalCheckoutOperationRepository(context);
        var op = await operations.GetByOperationIdAsync(accepted.OperationId);
        Assert.NotNull(op);
        Assert.Equal(CheckoutOperationStatus.Processing, op.Status);
        Assert.Equal(Now.AddMinutes(30), op.HoldExpiresAt);

        var outboxMessages = await context.RentalOutboxMessages.ToListAsync();
        Assert.Equal(2, outboxMessages.Count);
        Assert.All(outboxMessages, m =>
        {
            Assert.Equal(KafkaContractSpec.Topics.InventoryAllocationCommands, m.Topic);
            Assert.Equal(KafkaContractSpec.CommandTypes.CreateHold, m.MessageType);
            Assert.Equal(op.ExpectedRentalOrderId.ToString("D"), m.PartitionKey);
        });
    }

    [Fact]
    public async Task SubmitAsync_SendsInventoryTheLastRentalDayInclusive()
    {
        await using var context = _harness.NewContext();
        var service = CreateService(context);

        var result = await service.SubmitAsync(new CheckoutRenterContext(_renterId, true), Request(), Key);

        Assert.True(result.IsSuccess);
        // Giỏ thuê 12, 13, 14; đơn lưu [12, 15). Inventory nhận ngày cuối có tính: endDate = 14, không phải 15.
        Assert.All(await context.RentalOutboxMessages.ToListAsync(), m =>
        {
            using var envelope = System.Text.Json.JsonDocument.Parse(m.Payload);
            var payload = envelope.RootElement.GetProperty("payload");
            Assert.Equal("2026-10-12", payload.GetProperty("startDate").GetString());
            Assert.Equal("2026-10-14", payload.GetProperty("endDate").GetString());
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
        Assert.Equal(first.Value.OperationId, second.Value.OperationId);

        var outboxCount = await context.RentalOutboxMessages.CountAsync();
        Assert.Equal(2, outboxCount);
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

        var first = await service.SubmitAsync(new CheckoutRenterContext(_renterId, true), Request(), Key);
        Assert.True(first.IsSuccess);

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

        var statusResult = await service.GetOperationStatusAsync(submitResult.Value.OperationId, _renterId);
        Assert.True(statusResult.IsSuccess);
        Assert.Equal("Processing", statusResult.Value.Status);
    }

    [Fact]
    public async Task GetOperationStatusAsync_DifferentRenter_ReturnsNotFound()
    {
        await using var context = _harness.NewContext();
        var service = CreateService(context);

        var submitResult = await service.SubmitAsync(new CheckoutRenterContext(_renterId, true), Request(), Key);
        Assert.True(submitResult.IsSuccess);

        var statusResult = await service.GetOperationStatusAsync(submitResult.Value.OperationId, Guid.NewGuid());
        Assert.True(statusResult.IsFailure);
        Assert.Equal("CHECKOUT_OPERATION_NOT_FOUND", statusResult.Error.Code);
    }

    private sealed class BusyLockManager : IOrderLockManager
    {
        public Task<IAsyncDisposable> AcquireOrderLockAsync(Guid orderId, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            throw new TimeoutException();

        public Task<IAsyncDisposable> AcquireRenterLockAsync(Guid renterUserId, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            throw new TimeoutException();
    }
}
