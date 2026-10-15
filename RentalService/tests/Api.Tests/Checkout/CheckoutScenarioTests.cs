using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Checkout;

/// <summary>
/// Các kịch bản checkout bắt buộc theo AGENTS.md mục 25 mà test từng bước chưa phủ:
/// đơn nhiều supplier, hai renter tranh máy và hết hạn thanh toán của đơn tạo từ checkout.
/// </summary>
public sealed class CheckoutScenarioTests
{
    private static readonly DateTimeOffset Now = CheckoutTestHarness.Now;

    private readonly CheckoutTestHarness _harness = new();
    private readonly Guid _an = Guid.NewGuid();
    private readonly Guid _binh = Guid.NewGuid();

    [Fact]
    public async Task Checkout_WithItemsFromTwoSuppliers_CreatesOneOrderAndOneInvoiceWithALinePerSupplier()
    {
        Guid cameraSupplier = Guid.NewGuid();
        Guid lightSupplier = Guid.NewGuid();
        // Máy ảnh: 2 x 500.000 x 3 ngày = 3.000.000. Inventory chưa có tiền cọc nên cọc = 0.
        Guid camera = _harness.AddListing(cameraSupplier, 500_000m);
        // Đèn: 1 x 150.000 x 3 ngày = 450.000.
        Guid light = _harness.AddListing(lightSupplier, 150_000m);
        Guid[] cartItems = await _harness.SeedCartAsync(_an, (camera, 2), (light, 1));

        CheckoutResult result = await _harness.SubmitAsync(_an, "key-1", CheckoutTestHarness.Request(3_450_000m, cartItems));

        Assert.True(result.IsSuccess, result.Error.Code);
        RentalOrder order = Assert.Single(await _harness.LoadOrdersAsync());
        Assert.Equal(new[] { cameraSupplier, lightSupplier }.Order(), order.Items.Select(i => i.SupplierId).Order());
        Assert.Equal(3_450_000m, order.TotalPayableAmount);

        // Thanh toán một lần cho cả đơn; Payment nhận từng dòng kèm supplier để chia tiền về sau.
        CreateInvoiceRequest invoice = Assert.Single(_harness.Payment.InvoiceRequests);
        Assert.Equal(3_450_000m, invoice.TotalAmount);

        CreateInvoiceItemRequest cameraLine = Assert.Single(invoice.Items, i => i.SupplierId == cameraSupplier);
        Assert.Equal(3_000_000m, cameraLine.RentalAmount);
        Assert.Equal(0m, cameraLine.DepositAmount);
        Assert.Equal(order.Items.Single(i => i.SupplierId == cameraSupplier).Id, cameraLine.RentalItemId);

        CreateInvoiceItemRequest lightLine = Assert.Single(invoice.Items, i => i.SupplierId == lightSupplier);
        Assert.Equal(450_000m, lightLine.RentalAmount);
        Assert.Equal(0m, lightLine.DepositAmount);
        Assert.Equal(order.Items.Single(i => i.SupplierId == lightSupplier).Id, lightLine.RentalItemId);

        // Lưu cùng đơn: mỗi supplier một đơn con, đánh số theo thứ tự dòng trong giỏ.
        Assert.Equal(2, order.SubOrders.Count);
        RentalSubOrder cameraSubOrder = order.SubOrders.Single(s => s.SupplierId == cameraSupplier);
        RentalSubOrder lightSubOrder = order.SubOrders.Single(s => s.SupplierId == lightSupplier);
        Assert.Equal($"{order.OrderCode}-01", cameraSubOrder.SubOrderCode);
        Assert.Equal($"{order.OrderCode}-02", lightSubOrder.SubOrderCode);
        Assert.Equal(cameraSubOrder.Id, order.Items.Single(i => i.SupplierId == cameraSupplier).RentalSubOrderId);
        Assert.Equal(lightSubOrder.Id, order.Items.Single(i => i.SupplierId == lightSupplier).RentalSubOrderId);
    }

    [Fact]
    public async Task TwoRentersCompetingForTheSameUnits_OnlyOneGetsAnOrder_AndTheOtherKeepsNoHold()
    {
        Guid supplier = Guid.NewGuid();
        // Listing có 5 máy; An và Bình mỗi người đặt 3.
        Guid camera = _harness.AddListing(supplier, 500_000m, units: 5);
        Guid tripod = _harness.AddListing(supplier, 100_000m, units: 5);
        // Bước kiểm tra lịch của cả hai đều thấy còn đủ máy; chỉ Inventory lúc giữ lịch mới phân xử.
        _harness.Listings.AvailableQuantities[camera] = 5;

        Guid[] anCart = await _harness.SeedCartAsync(_an, (tripod, 1), (camera, 3));
        Guid[] binhCart = await _harness.SeedCartAsync(_binh, (tripod, 1), (camera, 3));
        // Chân máy 1 x 100.000 x 3 = 300.000; máy ảnh 3 x 500.000 x 3 = 4.500.000 (cọc = 0).
        const decimal total = 4_800_000m;

        CheckoutResult[] results = await Task.WhenAll(
            _harness.SubmitAsync(_an, "an-key", CheckoutTestHarness.Request(total, anCart)),
            _harness.SubmitAsync(_binh, "binh-key", CheckoutTestHarness.Request(total, binhCart)));

        CheckoutResult winner = Assert.Single(results, r => r.IsSuccess);
        CheckoutResult loser = Assert.Single(results, r => r.IsFailure);

        Assert.Equal(CheckoutErrors.HoldFailed, loser.Error);
        CheckoutHoldConflict conflict = Assert.Single(loser.HoldConflicts);
        Assert.Equal(CheckoutHoldConflictCode.InsufficientAvailability, conflict.Code);
        Assert.Equal(camera, conflict.EquipmentListingId);
        Assert.Equal(3, conflict.RequestedQuantity);

        RentalOrder order = Assert.Single(await _harness.LoadOrdersAsync());
        Assert.Equal(winner.Order!.Id, order.Id);
        Assert.Single(_harness.Payment.InvoiceRequests);

        // Người thua đã giữ được chân máy trước khi hết máy ảnh: hold đó phải được nhả, giỏ còn nguyên.
        Assert.Equal(3, _harness.Inventory.HeldUnits(camera));
        Assert.Equal(1, _harness.Inventory.HeldUnits(tripod));
        Guid loserId = order.RenterId == _an ? _binh : _an;
        Assert.Equal(2, await _harness.CountCartItemsAsync(loserId));
        Assert.Equal(0, await _harness.CountCartItemsAsync(order.RenterId));
    }

    [Fact]
    public async Task DatabaseFailureWhileSavingTheOrder_ReleasesHolds_AndTheRenterCanCheckoutAgain()
    {
        Guid supplier = Guid.NewGuid();
        Guid camera = _harness.AddListing(supplier, 500_000m, units: 2);
        Guid[] cart = await _harness.SeedCartAsync(_an, (camera, 2));
        CheckoutRequest request = CheckoutTestHarness.Request(3_000_000m, cart);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _harness.SubmitAsync(
            _an, "key-1", request, writerUnitOfWork: context => new FailingUnitOfWork(context)));

        Assert.Equal(0, _harness.Inventory.HeldUnits(camera));
        Assert.Empty(await _harness.LoadOrdersAsync());
        Assert.Empty(_harness.Payment.InvoiceRequests);
        Assert.Equal(1, await _harness.CountCartItemsAsync(_an));

        // Khóa của renter đã được trả và máy đã được nhả: lần đặt lại thành công.
        CheckoutResult retried = await _harness.SubmitAsync(_an, "key-2", request);

        Assert.True(retried.IsSuccess, retried.Error.Code);
        Assert.Equal(2, _harness.Inventory.HeldUnits(camera));
        Assert.Single(await _harness.LoadOrdersAsync());
    }

    [Fact]
    public async Task PaymentTimeout_ExpiresCheckoutOrder_CancelsInvoice_ReleasesHolds_AndUnitsCanBeBookedAgain()
    {
        Guid supplier = Guid.NewGuid();
        Guid camera = _harness.AddListing(supplier, 500_000m, units: 2);
        Guid[] anCart = await _harness.SeedCartAsync(_an, (camera, 2));
        Guid[] binhCart = await _harness.SeedCartAsync(_binh, (camera, 2));
        const decimal total = 3_000_000m;

        CheckoutResult anResult = await _harness.SubmitAsync(_an, "an-key", CheckoutTestHarness.Request(total, anCart));
        Assert.True(anResult.IsSuccess, anResult.Error.Code);
        Assert.Equal(2, _harness.Inventory.HeldUnits(camera));

        // Chưa tới hạn: lượt quét không đụng tới đơn.
        _harness.Clock.UtcNow = Now.AddMinutes(29);
        Assert.Equal(0, await _harness.RunPaymentExpiryScanAsync());
        Assert.Equal(RentalOrderStatus.PendingPayment, Assert.Single(await _harness.LoadOrdersAsync()).Status);

        _harness.Clock.UtcNow = Now.AddMinutes(31);
        Assert.Equal(1, await _harness.RunPaymentExpiryScanAsync());

        RentalOrder expired = Assert.Single(await _harness.LoadOrdersAsync());
        Assert.Equal(RentalOrderStatus.Expired, expired.Status);
        Assert.All(expired.Items, i => Assert.Equal(RentalItemStatus.Expired, i.Status));
        Assert.Equal("INV-1", Assert.Single(_harness.Payment.CancelRequests).InvoiceRef);
        Assert.Equal(0, _harness.Inventory.HeldUnits(camera));
        Assert.Empty(await _harness.LoadPendingOperationsAsync());

        // Lượt quét sau không xử lý lại đơn đã hết hạn.
        Assert.Equal(0, await _harness.RunPaymentExpiryScanAsync());
        Assert.Single(_harness.Payment.CancelRequests);

        // Máy đã được nhả nên Bình đặt được đúng 2 máy đó.
        CheckoutResult binhResult = await _harness.SubmitAsync(_binh, "binh-key", CheckoutTestHarness.Request(total, binhCart));
        Assert.True(binhResult.IsSuccess, binhResult.Error.Code);
        Assert.Equal(2, _harness.Inventory.HeldUnits(camera));
    }

    [Fact]
    public async Task PaymentTimeout_ForOrderWithoutInvoice_ReleasesHolds_AndLeavesNothingForTheRecoveryWorker()
    {
        Guid supplier = Guid.NewGuid();
        Guid camera = _harness.AddListing(supplier, 500_000m, units: 2);
        Guid tripod = _harness.AddListing(supplier, 100_000m);
        Guid[] cart = await _harness.SeedCartAsync(_an, (camera, 2), (tripod, 1));

        // Payment sập suốt thời gian chờ thanh toán: đơn được tạo nhưng không có hóa đơn.
        _harness.Payment.AlwaysFailWith = FakePaymentRentalClient.Down;
        CheckoutResult failed = await _harness.SubmitAsync(_an, "key-1", CheckoutTestHarness.Request(3_000_000m, cart[0]));
        Assert.Equal(CheckoutErrors.InvoiceTemporarilyUnavailable, failed.Error);
        Assert.Equal(2, _harness.Inventory.HeldUnits(camera));

        _harness.Clock.UtcNow = Now.AddMinutes(31);
        Assert.Equal(1, await _harness.RunPaymentExpiryScanAsync());

        RentalOrder expired = Assert.Single(await _harness.LoadOrdersAsync());
        Assert.Equal(RentalOrderStatus.Expired, expired.Status);
        Assert.Null(expired.InvoiceRef);
        Assert.Equal(0, _harness.Inventory.HeldUnits(camera));

        // Không có hóa đơn thì không gọi Payment hủy và không để lại việc treo cho OperationRecoveryWorker.
        Assert.Empty(_harness.Payment.CancelRequests);
        Assert.Empty(await _harness.LoadPendingOperationsAsync());

        // Renter không còn bị chặn bởi đơn cũ: đặt được dòng còn lại trong giỏ.
        _harness.Payment.AlwaysFailWith = null;
        CheckoutResult next = await _harness.SubmitAsync(_an, "key-2", CheckoutTestHarness.Request(300_000m, cart[1]));
        Assert.True(next.IsSuccess, next.Error.Code);
        Assert.Equal(2, (await _harness.LoadOrdersAsync()).Count);
    }

    /// <summary>Giả lập database sập đúng lúc lưu đơn.</summary>
    private sealed class FailingUnitOfWork(RentalServiceDbContext inner) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated database outage.");

        public void DiscardChanges() => inner.DiscardChanges();
        public Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            inner.BeginTransactionAsync(cancellationToken);
    }
}
