using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Services.Checkout;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Checkout;

/// <summary>
/// Chạy cả luồng checkout (chuẩn bị, giữ lịch, lưu đơn, hóa đơn) trên EF InMemory với Inventory và Payment giả.
/// Mỗi lần gửi dùng một DbContext mới, giống một HTTP request.
/// </summary>
public sealed class CheckoutServiceTests
{
    // 10:00 ngày 09/10/2026 giờ Việt Nam.
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Start = new(2026, 10, 12);
    // Giỏ lưu ngày trả có tính: giao 12/10, trả 14/10 là 3 ngày.
    private static readonly DateOnly End = new(2026, 10, 14);

    private const string Key = "checkout-key-1";
    private const string OtherKey = "checkout-key-2";

    // Máy ảnh: 2 máy x 500.000 x 3 ngày + cọc 2 x 2.000.000 = 7.000.000.
    // Ống kính: 1 cái x 200.000 x 3 ngày + cọc 1.000.000 = 1.600.000.
    private const decimal CameraAndLensTotal = 8_600_000m;
    // Chân máy: 1 cái x 100.000 x 3 ngày + cọc 500.000 = 800.000.
    private const decimal TripodTotal = 800_000m;

    private readonly string _dbName = $"CheckoutService_{Guid.NewGuid():N}";
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _supplierId = Guid.NewGuid();
    private readonly Guid _equipmentTypeId = Guid.NewGuid();
    private readonly Guid _brandId = Guid.NewGuid();
    private readonly Guid _cameraListingId = Guid.NewGuid();
    private readonly Guid _lensListingId = Guid.NewGuid();
    private readonly Guid _tripodListingId = Guid.NewGuid();

    private readonly FakeInventoryListingClient _listings = new();
    private readonly FakeCatalogReferenceClient _catalog = new();
    private readonly FakeInventoryRentalClient _inventory = new();
    private readonly FakePaymentRentalClient _payment = new();
    private readonly FakeOrderCodeGenerator _orderCodes = new();
    private readonly FakeOrderLockManager _locks = new();
    private readonly AdjustableTimeProvider _clock = new(Now);

    private readonly Guid _cameraItemId;
    private readonly Guid _lensItemId;
    private readonly Guid _tripodItemId;

    public CheckoutServiceTests()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, Now).Value;
        _cameraItemId = cart.AddItem(Guid.NewGuid(), _cameraListingId, _supplierId, 2, Start, End, Now).Value.Id;
        _lensItemId = cart.AddItem(Guid.NewGuid(), _lensListingId, _supplierId, 1, Start, End, Now).Value.Id;
        _tripodItemId = cart.AddItem(Guid.NewGuid(), _tripodListingId, _supplierId, 1, Start, End, Now).Value.Id;

        using RentalServiceDbContext context = NewContext();
        context.RentalCarts.Add(cart);
        context.SaveChanges();

        AddListing(_cameraListingId, "Sony A7 IV", 500_000m, 2_000_000m);
        AddListing(_lensListingId, "Ống kính 24-70", 200_000m, 1_000_000m);
        AddListing(_tripodListingId, "Chân máy", 100_000m, 500_000m);
        _catalog.EquipmentTypeNames[_equipmentTypeId] = "Máy ảnh";
        _catalog.BrandNames[_brandId] = "Sony";
    }

    private void AddListing(Guid listingId, string title, decimal dailyRate, decimal depositPerUnit)
    {
        _listings.Listings[listingId] = TestListings.Create(
            listingId,
            _supplierId,
            true,
            title,
            _equipmentTypeId,
            _brandId,
            "Model",
            1,
            """{"sensor":"full-frame"}""",
            Guid.NewGuid(),
            dailyRate,
            depositPerUnit,
            "VND",
            "Quận 1, TP.HCM",
            "listing-v1");
        _listings.AvailableQuantities[listingId] = 10;
    }

    private RentalServiceDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalServiceDbContext>().UseInMemoryDatabase(_dbName).Options);

    private CheckoutRequest Request(decimal expectedTotal = CameraAndLensTotal, params Guid[] cartItemIds) => new(
        cartItemIds.Length == 0 ? [_cameraItemId, _lensItemId] : cartItemIds,
        new("Hồ Chí Minh", "123 Nguyễn Huệ, Quận 1", "Nguyễn Văn A", "0901234567", null),
        new("VCB", "0123456789", "NGUYEN VAN A"),
        "v1.0",
        true,
        expectedTotal);

    private async Task<CheckoutResult> SubmitAsync(
        string key,
        CheckoutRequest? request = null,
        Guid? renterId = null,
        IOrderLockManager? locks = null)
    {
        await using RentalServiceDbContext context = NewContext();
        RentalOrderRepository orders = new(context);
        RentalCartRepository carts = new(context);
        CheckoutHoldOrchestrator holds = new(_inventory, NullLogger<CheckoutHoldOrchestrator>.Instance);

        CheckoutService service = new(
            new CheckoutPreparationService(
                carts,
                orders,
                _listings,
                _catalog,
                Microsoft.Extensions.Options.Options.Create(new RentalTermsOptions
                {
                    CurrentVersion = "v1.0",
                    DocumentRef = "terms/v1.0.pdf",
                    ContentHash = "hash123456"
                }),
                _clock),
            holds,
            new CheckoutOrderWriter(orders, carts, _orderCodes, holds, context, NullLogger<CheckoutOrderWriter>.Instance),
            new CheckoutInvoiceIssuer(_payment, orders, context, _clock, NullLogger<CheckoutInvoiceIssuer>.Instance),
            orders,
            locks ?? _locks,
            _clock);

        return await service.SubmitAsync(new(renterId ?? _renterId, true), request ?? Request(), key);
    }

    private async Task<List<RentalOrder>> LoadOrdersAsync()
    {
        await using RentalServiceDbContext context = NewContext();
        return await context.RentalOrders.Include(o => o.Items).ToListAsync();
    }

    private async Task<int> CountCartItemsAsync()
    {
        await using RentalServiceDbContext context = NewContext();
        return await context.RentalCartItems.CountAsync();
    }

    [Fact]
    public async Task Submit_CreatesPendingPaymentOrder_WithInvoiceForRentalPlusDeposit()
    {
        CheckoutResult result = await SubmitAsync(Key);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsReplay);

        RentalOrder order = Assert.Single(await LoadOrdersAsync());
        Assert.Equal(result.Order!.Id, order.Id);
        Assert.Equal(RentalOrderStatus.PendingPayment, order.Status);
        Assert.Equal(Now.AddMinutes(30), order.PaymentDeadline);
        Assert.Equal("INV-1", order.InvoiceRef);
        Assert.Equal(CameraAndLensTotal, order.TotalPayableAmount);

        CreateInvoiceRequest invoice = Assert.Single(_payment.InvoiceRequests);
        Assert.Equal(order.Id, invoice.RentalOrderId);
        Assert.Equal(order.OrderCode, invoice.OrderCode);
        Assert.Equal(_renterId, invoice.RenterId);
        Assert.Equal(order.PaymentDeadline, invoice.PaymentDeadline);
        Assert.Equal(3_600_000m, invoice.Items.Sum(i => i.RentalAmount));
        Assert.Equal(5_000_000m, invoice.Items.Sum(i => i.DepositAmount));
        Assert.Equal(CameraAndLensTotal, invoice.TotalAmount);

        Assert.Equal(2, _inventory.HoldRequests.Count);
        Assert.Empty(_inventory.Releases);
        Assert.Equal(1, await CountCartItemsAsync());
    }

    [Fact]
    public async Task Submit_ScopesTheIdempotencyKeyToTheRenter()
    {
        CheckoutResult result = await SubmitAsync(Key);

        Assert.NotEqual(Key, result.Order!.CheckoutOperationId);
        Assert.Equal(CheckoutRules.DeriveOperationId(_renterId, Key), result.Order.CheckoutOperationId);
        Assert.NotEqual(
            CheckoutRules.DeriveOperationId(_renterId, Key),
            CheckoutRules.DeriveOperationId(Guid.NewGuid(), Key));
    }

    [Fact]
    public async Task Submit_WithSameKey_ReturnsTheSameOrder_WithoutHoldingOrInvoicingAgain()
    {
        CheckoutResult first = await SubmitAsync(Key);

        CheckoutResult second = await SubmitAsync(Key);

        Assert.True(second.IsSuccess);
        Assert.True(second.IsReplay);
        Assert.Equal(first.Order!.Id, second.Order!.Id);
        Assert.Equal("INV-1", second.Order.InvoiceRef);

        Assert.Single(await LoadOrdersAsync());
        Assert.Equal(2, _inventory.HoldRequests.Count);
        Assert.Equal(2, _inventory.CreatedGroupCount);
        Assert.Single(_payment.InvoiceRequests);
        // Lần gửi đầu đọc cả hai Listing trong một lần gọi; lần gửi lại không đọc Inventory.
        Assert.Equal(1, _listings.ListingReadCount);
        Assert.Equal(2, _listings.RequestedListingIds.Count);
    }

    [Fact]
    public async Task Submit_DoubleClick_SecondRequestWaitsForTheFirst_ThenReturnsItsOrder()
    {
        TaskCompletionSource paymentGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _payment.Gate = paymentGate.Task;

        Task<CheckoutResult> first = SubmitAsync(Key);
        Task<CheckoutResult> second = SubmitAsync(Key);

        // Request đầu đang chờ Payment và giữ khóa; request sau chưa được chạm vào Inventory.
        Assert.True(SpinWait.SpinUntil(() => _payment.InvoiceRequests.Count == 1, TimeSpan.FromSeconds(5)));
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(2, _inventory.HoldRequests.Count);

        paymentGate.SetResult();
        CheckoutResult[] results = await Task.WhenAll(first, second);

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Single(results, r => r.IsReplay);
        Assert.Equal(results[0].Order!.Id, results[1].Order!.Id);

        Assert.Single(await LoadOrdersAsync());
        Assert.Equal(2, _inventory.HoldRequests.Count);
        Assert.Empty(_inventory.Releases);
        Assert.Equal(1, _payment.CreatedInvoiceCount);
        Assert.Single(_payment.InvoiceRequests);
    }

    [Fact]
    public async Task Submit_WithSameKeyButDifferentCartItems_IsRejected()
    {
        await SubmitAsync(Key);

        CheckoutResult result = await SubmitAsync(Key, Request(TripodTotal, _tripodItemId));

        Assert.Equal(CheckoutErrors.IdempotencyKeyReused, result.Error);
        Assert.Null(result.Order);
        Assert.Single(await LoadOrdersAsync());
        Assert.Equal(2, _inventory.HoldRequests.Count);
    }

    [Fact]
    public async Task Submit_WithSameKeyFromAnotherRenter_DoesNotReturnTheFirstRentersOrder()
    {
        await SubmitAsync(Key);

        CheckoutResult result = await SubmitAsync(Key, renterId: Guid.NewGuid());

        Assert.Equal(RentalErrors.CartNotFound, result.Error);
        Assert.Null(result.Order);
    }

    [Fact]
    public async Task Submit_KeepsTheOrderAndItsHolds_WhenPaymentIsDown()
    {
        _payment.AlwaysFailWith = FakePaymentRentalClient.Down;

        CheckoutResult result = await SubmitAsync(Key);

        Assert.Equal(CheckoutErrors.InvoiceTemporarilyUnavailable, result.Error);
        Assert.True(result.IsRetryable);
        Assert.NotNull(result.Order);

        RentalOrder order = Assert.Single(await LoadOrdersAsync());
        Assert.Equal(result.Order.OrderCode, order.OrderCode);
        Assert.Equal(RentalOrderStatus.PendingPayment, order.Status);
        Assert.Null(order.InvoiceRef);
        Assert.Empty(_inventory.Releases);
        Assert.Equal(1, await CountCartItemsAsync());
    }

    [Fact]
    public async Task Submit_RetryWithSameKey_OnlyCreatesTheMissingInvoice()
    {
        _payment.AlwaysFailWith = FakePaymentRentalClient.Down;
        CheckoutResult failed = await SubmitAsync(Key);
        _payment.AlwaysFailWith = null;

        CheckoutResult retried = await SubmitAsync(Key);

        Assert.True(retried.IsSuccess);
        Assert.True(retried.IsReplay);
        Assert.Equal(failed.Order!.Id, retried.Order!.Id);

        RentalOrder order = Assert.Single(await LoadOrdersAsync());
        Assert.Equal("INV-1", order.InvoiceRef);
        Assert.Equal(2, _inventory.HoldRequests.Count);
        Assert.Empty(_inventory.Releases);
        Assert.Equal(1, _payment.CreatedInvoiceCount);
    }

    [Fact]
    public async Task Submit_WithNewKey_CreatesTheMissingInvoiceOfThePendingOrder_AndStillBlocksANewCheckout()
    {
        _payment.AlwaysFailWith = FakePaymentRentalClient.Down;
        CheckoutResult failed = await SubmitAsync(Key);
        _payment.AlwaysFailWith = null;

        CheckoutResult result = await SubmitAsync(OtherKey, Request(TripodTotal, _tripodItemId));

        Assert.Equal(CheckoutErrors.PendingOrderExists, result.Error);
        Assert.Equal(failed.Order!.OrderCode, result.PendingOrderCode);

        RentalOrder order = Assert.Single(await LoadOrdersAsync());
        Assert.Equal("INV-1", order.InvoiceRef);
        Assert.Equal(2, _inventory.HoldRequests.Count);
        Assert.Equal(1, await CountCartItemsAsync());
    }

    [Fact]
    public async Task Submit_WithNewKey_StillBlocks_WhenThePendingOrderInvoiceCannotBeCreatedYet()
    {
        _payment.AlwaysFailWith = FakePaymentRentalClient.Down;
        CheckoutResult failed = await SubmitAsync(Key);

        CheckoutResult result = await SubmitAsync(OtherKey, Request(TripodTotal, _tripodItemId));

        Assert.Equal(CheckoutErrors.PendingOrderExists, result.Error);
        Assert.Equal(failed.Order!.OrderCode, result.PendingOrderCode);
        Assert.Null(Assert.Single(await LoadOrdersAsync()).InvoiceRef);
        Assert.Equal(2, _inventory.HoldRequests.Count);
    }

    [Fact]
    public async Task Submit_WithNewKey_DoesNotInvoiceAgain_WhenThePendingOrderAlreadyHasAnInvoice()
    {
        await SubmitAsync(Key);

        CheckoutResult result = await SubmitAsync(OtherKey, Request(TripodTotal, _tripodItemId));

        Assert.Equal(CheckoutErrors.PendingOrderExists, result.Error);
        Assert.Single(_payment.InvoiceRequests);
        Assert.Single(await LoadOrdersAsync());
    }

    [Fact]
    public async Task Submit_ReplayAfterPaymentDeadlineWithoutInvoice_ReturnsOrderExpired()
    {
        _payment.AlwaysFailWith = FakePaymentRentalClient.Down;
        await SubmitAsync(Key);
        int paymentCallsBefore = _payment.InvoiceRequests.Count;
        _payment.AlwaysFailWith = null;
        _clock.UtcNow = Now.AddMinutes(31);

        CheckoutResult result = await SubmitAsync(Key);

        Assert.Equal(CheckoutErrors.OrderExpired, result.Error);
        Assert.False(result.IsRetryable);
        Assert.NotNull(result.Order);
        Assert.Equal(paymentCallsBefore, _payment.InvoiceRequests.Count);
    }

    [Fact]
    public async Task Submit_ReturnsCurrentQuote_AndHoldsNothing_WhenPriceChanged()
    {
        CheckoutResult result = await SubmitAsync(Key, Request(expectedTotal: CameraAndLensTotal - 1));

        Assert.Equal(CheckoutErrors.PriceChanged, result.Error);
        Assert.Equal(CameraAndLensTotal, result.CurrentQuote!.TotalAmount);
        Assert.Null(result.Order);
        Assert.Empty(_inventory.HoldRequests);
        Assert.Empty(_payment.InvoiceRequests);
        Assert.Empty(await LoadOrdersAsync());
    }

    [Fact]
    public async Task Submit_ReturnsFieldErrors_WhenRequestIsInvalid()
    {
        CheckoutResult result = await SubmitAsync(Key, Request() with { TermsAccepted = false });

        Assert.Equal(CheckoutErrors.InvalidRequest, result.Error);
        Assert.Contains(result.FieldErrors, e => e.Field == "termsAccepted");
        Assert.Empty(_inventory.HoldRequests);
    }

    [Fact]
    public async Task Submit_ReturnsHoldConflicts_ReleasesHeldLines_AndCreatesNoOrderOrInvoice()
    {
        _inventory.HoldErrors[_lensListingId] = Error.Conflict(
            InventoryContractSpec.ErrorCodes.InsufficientAvailability, "Không đủ máy.");

        CheckoutResult result = await SubmitAsync(Key);

        Assert.Equal(CheckoutErrors.HoldFailed, result.Error);
        CheckoutHoldConflict conflict = Assert.Single(result.HoldConflicts);
        Assert.Equal(_lensItemId, conflict.CartItemId);
        Assert.Equal(CheckoutHoldConflictCode.InsufficientAvailability, conflict.Code);

        Assert.Equal(_inventory.GroupIdFor(_cameraListingId), Assert.Single(_inventory.Releases).AllocationGroupId);
        Assert.Empty(await LoadOrdersAsync());
        Assert.Empty(_payment.InvoiceRequests);
        Assert.Equal(3, await CountCartItemsAsync());
    }

    [Fact]
    public async Task Submit_ReturnsInProgress_WhenAnotherCheckoutOfTheRenterHoldsTheLock()
    {
        CheckoutResult result = await SubmitAsync(Key, locks: new BusyLockManager());

        Assert.Equal(CheckoutErrors.InProgress, result.Error);
        Assert.True(result.IsRetryable);
        Assert.Equal(0, _listings.ListingReadCount);
        Assert.Empty(_inventory.HoldRequests);
        Assert.Empty(await LoadOrdersAsync());
    }

    [Fact]
    public async Task Submit_LocksByRenter()
    {
        RecordingLockManager locks = new();

        await SubmitAsync(Key, locks: locks);

        Assert.Equal(_renterId, Assert.Single(locks.LockedIds));
    }

    private sealed class BusyLockManager : IOrderLockManager
    {
        public Task<IAsyncDisposable> AcquireOrderLockAsync(
            Guid orderId, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            throw new TimeoutException("Simulated lock timeout.");
    }

    private sealed class RecordingLockManager : IOrderLockManager
    {
        private readonly FakeOrderLockManager _inner = new();

        public List<Guid> LockedIds { get; } = [];

        public Task<IAsyncDisposable> AcquireOrderLockAsync(
            Guid orderId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            LockedIds.Add(orderId);
            return _inner.AcquireOrderLockAsync(orderId, timeout, cancellationToken);
        }
    }
}
