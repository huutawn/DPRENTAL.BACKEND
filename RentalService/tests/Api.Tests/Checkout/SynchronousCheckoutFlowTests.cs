using System.Net;
using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.IServices.Checkout;
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

public sealed class SynchronousCheckoutFlowTests
{
    private static readonly DateTimeOffset Now = CheckoutTestHarness.Now;
    private readonly CheckoutTestHarness _harness = new();
    private readonly Guid _renterId = Guid.NewGuid();

    public SynchronousCheckoutFlowTests()
    {
        _harness.Auth.UserId = _renterId;
        _harness.Auth.IsProfileCompleted = true;
    }

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
                new RenterAddressRepository(context),
                _harness.Listings,
                _harness.Auth,
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
    public async Task SubmitAsync_SingleLine_Returns200WithQrUrlImmediately()
    {
        Guid supplier = Guid.NewGuid();
        Guid camera = _harness.AddListing(supplier, 500_000m, units: 5);
        Guid[] cart = await _harness.SeedCartAsync(_renterId, (camera, 1));
        CheckoutRequest request = CheckoutTestHarness.Request(1_500_000m, cart);

        await using var context = _harness.NewContext();
        var service = CreateService(context);

        var result = await service.SubmitAsync(new CheckoutRenterContext(_renterId), request, "single-line-key");

        Assert.True(result.IsSuccess, result.Error.Message);
        var resp = result.Value;
        Assert.NotEqual(Guid.Empty, resp.OrderId);
        Assert.StartsWith("DH-", resp.OrderCode);
        Assert.Equal("PendingPayment", resp.Status);
        Assert.Equal(1_500_000m, resp.TotalAmount);
        Assert.False(resp.IsReplay);
        Assert.NotNull(resp.Payment);
        Assert.NotEmpty(resp.Payment.QrUrl);
        Assert.Equal("Pending", resp.Payment.Status);
        Assert.NotEmpty(resp.Payment.InvoiceRef);
        Assert.NotEmpty(resp.Payment.Bank);
        Assert.NotEmpty(resp.Payment.Account);
        Assert.NotEmpty(resp.Payment.TransferContent);
    }

    [Fact]
    public async Task SubmitAsync_MultipleLines_Returns200WithQrUrlImmediately()
    {
        Guid supplier1 = Guid.NewGuid();
        Guid supplier2 = Guid.NewGuid();
        Guid camera = _harness.AddListing(supplier1, 500_000m, units: 5);
        Guid lens = _harness.AddListing(supplier2, 200_000m, units: 3);
        Guid[] cart = await _harness.SeedCartAsync(_renterId, (camera, 2), (lens, 1));
        // 2 x 500k x 3 = 3m; 1 x 200k x 3 = 600k -> 3.6m
        CheckoutRequest request = CheckoutTestHarness.Request(3_600_000m, cart);

        await using var context = _harness.NewContext();
        var service = CreateService(context);

        var result = await service.SubmitAsync(new CheckoutRenterContext(_renterId), request, "multi-line-key");

        Assert.True(result.IsSuccess, result.Error.Message);
        var resp = result.Value;
        Assert.Equal("PendingPayment", resp.Status);
        Assert.Equal(3_600_000m, resp.TotalAmount);
        Assert.NotNull(resp.Payment);
        Assert.NotEmpty(resp.Payment.QrUrl);
        Assert.Equal(2, _harness.Inventory.HoldRequests.Count);
    }

    [Fact]
    public async Task SubmitAsync_ZeroTotalAmount_Returns200WithPaidStatusAndNullPayment()
    {
        Guid supplier = Guid.NewGuid();
        Guid camera = _harness.AddListing(supplier, 500_000m, units: 2);

        string opId = CheckoutRules.DeriveOperationId(_renterId, "zero-amount-key");
        Guid orderId = Guid.NewGuid();

        var zeroItem = new InitialRentalItemDto(
            Guid.NewGuid(), camera, supplier, 1,
            new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 15),
            "Promo Item", null, null, 0m, 0m, "grp-promo",
            [new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
            TestListingSnapshots.Default);

        var order = RentalOrder.Create(
            orderId, "DH-2026-000099", _renterId, "VND", "Delivery",
            "Nguyễn Văn A", "0901234567", "123 Nguyễn Huệ", null, null,
            "v1.0", "terms.pdf", "hash", opId, Now, [zeroItem]).Value;

        var op = new RentalCheckoutOperation(
            opId, _renterId, "fp", orderId, Now.AddMinutes(30), "{}", "[]", Now);

        await using (var context = _harness.NewContext())
        {
            context.RentalOrders.Add(order);
            context.RentalCheckoutOperations.Add(op);
            await context.SaveChangesAsync();
        }

        await using (var context = _harness.NewContext())
        {
            var service = CreateService(context);
            var result = await service.ContinueOperationAsync(opId);

            Assert.True(result.IsSuccess, result.Error.Message);
            var resp = result.Value;
            Assert.Equal("Paid", resp.Status);
            Assert.Equal(0m, resp.TotalAmount);
            Assert.Null(resp.Payment);
        }
    }

    [Fact]
    public async Task SubmitAsync_MissingQrUrl_DoesNotReturn200_ReturnsCheckoutNotReady()
    {
        Guid supplier = Guid.NewGuid();
        Guid camera = _harness.AddListing(supplier, 500_000m, units: 2);
        Guid[] cart = await _harness.SeedCartAsync(_renterId, (camera, 1));
        CheckoutRequest request = CheckoutTestHarness.Request(1_500_000m, cart);

        // Giả lập Payment trả thành công nhưng thiếu QR url
        _harness.Payment.Tamper = resp => resp with { QrUrl = null };

        await using var context = _harness.NewContext();
        var service = CreateService(context);

        var result = await service.SubmitAsync(new CheckoutRenterContext(_renterId), request, "missing-qr-key");

        Assert.True(result.IsFailure);
        Assert.Equal(CheckoutErrors.CheckoutNotReady, result.Error);
        Assert.True(result.Failure!.IsRetryable);
    }

    [Fact]
    public async Task SubmitAsync_OneLineOutOfStock_FailsWithoutCreatingInvoice_AndCompensatesHeldLines()
    {
        Guid supplier = Guid.NewGuid();
        Guid availableCamera = _harness.AddListing(supplier, 500_000m, units: 5);
        Guid outOfStockLens = _harness.AddListing(supplier, 200_000m, units: 1);
        _harness.Inventory.Capacities[availableCamera] = 5;
        _harness.Inventory.Capacities[outOfStockLens] = 0; // hết hàng

        Guid[] cart = await _harness.SeedCartAsync(_renterId, (availableCamera, 1), (outOfStockLens, 1));
        CheckoutRequest request = CheckoutTestHarness.Request(2_100_000m, cart);

        await using var context = _harness.NewContext();
        var service = CreateService(context);

        var result = await service.SubmitAsync(new CheckoutRenterContext(_renterId), request, "out-of-stock-key");

        Assert.True(result.IsFailure);
        Assert.Equal(InventoryContractSpec.ErrorCodes.InsufficientAvailability, result.Error.Code);

        // Không tạo invoice
        Assert.Empty(_harness.Payment.InvoiceRequests);

        // Đã enqueue compensation release cho dòng đầu tiên
        var outboxReleases = await context.RentalOutboxMessages
            .Where(m => m.MessageType == KafkaContractSpec.CommandTypes.ReleaseAllocation)
            .ToListAsync();
        Assert.Single(outboxReleases);

        // Chạy bù trừ release qua Outbox
        await _harness.Completion.RunInventoryAsync(Guid.Empty);
        Assert.Equal(0, _harness.Inventory.HeldUnits(availableCamera));
    }

    [Fact]
    public async Task SubmitAsync_ReplayWhenPaid_ReturnsConflict_DoesNotReturnOldQr()
    {
        Guid supplier = Guid.NewGuid();
        Guid camera = _harness.AddListing(supplier, 500_000m, units: 5);
        Guid[] cart = await _harness.SeedCartAsync(_renterId, (camera, 1));
        CheckoutRequest request = CheckoutTestHarness.Request(1_500_000m, cart);

        await using (var context = _harness.NewContext())
        {
            var service = CreateService(context);
            var initial = await service.SubmitAsync(new CheckoutRenterContext(_renterId), request, "replay-paid-key");
            Assert.True(initial.IsSuccess);

            // Đơn chuyển sang Paid
            var order = await context.RentalOrders.FirstAsync(o => o.Id == initial.Value.OrderId);
            order.MarkAsPaid("INV-1", "PAY-001", Now.AddMinutes(5), Now.AddMinutes(5));
            await context.SaveChangesAsync();
        }

        await using (var context = _harness.NewContext())
        {
            var service = CreateService(context);
            var replay = await service.SubmitAsync(new CheckoutRenterContext(_renterId), request, "replay-paid-key");

            Assert.True(replay.IsFailure);
            Assert.Equal("ORDER_ALREADY_PAID", replay.Error.Code);
        }
    }

    [Fact]
    public async Task SubmitAsync_ReplayWhenCancelledOrExpired_ReturnsConflict_DoesNotReturnOldQr()
    {
        Guid supplier = Guid.NewGuid();
        Guid camera = _harness.AddListing(supplier, 500_000m, units: 5);
        Guid[] cart = await _harness.SeedCartAsync(_renterId, (camera, 1));
        CheckoutRequest request = CheckoutTestHarness.Request(1_500_000m, cart);

        await using (var context = _harness.NewContext())
        {
            var service = CreateService(context);
            var initial = await service.SubmitAsync(new CheckoutRenterContext(_renterId), request, "replay-exp-key");
            Assert.True(initial.IsSuccess);

            var order = await context.RentalOrders.FirstAsync(o => o.Id == initial.Value.OrderId);
            order.MarkPaymentExpired(Now.AddMinutes(31), "expire-op");
            await context.SaveChangesAsync();
        }

        await using (var context = _harness.NewContext())
        {
            var service = CreateService(context);
            var replay = await service.SubmitAsync(new CheckoutRenterContext(_renterId), request, "replay-exp-key");

            Assert.True(replay.IsFailure);
            Assert.Equal("ORDER_ALREADY_EXPIRED", replay.Error.Code);
        }
    }

    [Fact]
    public async Task ContinueOperationAsync_ResumesFromHoldCheckpointToCompletion()
    {
        Guid supplier = Guid.NewGuid();
        Guid camera = _harness.AddListing(supplier, 500_000m, units: 5);
        Guid[] cart = await _harness.SeedCartAsync(_renterId, (camera, 1));
        CheckoutRequest request = CheckoutTestHarness.Request(1_500_000m, cart);

        string opId = CheckoutRules.DeriveOperationId(_renterId, "resume-key");

        // Giả lập dừng sau checkpoint: operation đã lưu nhưng chưa tạo order
        await using (var context = _harness.NewContext())
        {
            var service = CreateService(context);
            _harness.Payment.AlwaysFailWith = FakePaymentRentalClient.Down;
            var res = await service.SubmitAsync(new CheckoutRenterContext(_renterId), request, "resume-key");
            Assert.True(res.IsFailure);
            Assert.Equal(CheckoutErrors.CheckoutNotReady, res.Error);
        }

        // Khôi phục Payment
        _harness.Payment.AlwaysFailWith = null;

        // Worker tiếp tục operation từ checkpoint
        await using (var context = _harness.NewContext())
        {
            var service = CreateService(context);
            var continued = await service.ContinueOperationAsync(opId);

            Assert.True(continued.IsSuccess, continued.Error.Message);
            Assert.Equal("PendingPayment", continued.Value.Status);
            Assert.NotNull(continued.Value.Payment);
            Assert.NotEmpty(continued.Value.Payment.QrUrl);
        }
    }
}
