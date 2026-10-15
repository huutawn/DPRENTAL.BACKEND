using System.Reflection;
using System.Text.Json;
using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.Errors;
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

public sealed class CheckoutOrderWriterTests
{
    // 10:00 ngày 09/10/2026 giờ Việt Nam.
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Start = new(2026, 10, 12);
    private static readonly DateOnly End = new(2026, 10, 15);
    private const string OperationId = "idem-key-1";

    private readonly string _dbName = $"CheckoutWriter_{Guid.NewGuid():N}";
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _cartId = Guid.NewGuid();
    private readonly FakeInventoryRentalClient _inventory = new();
    private readonly SequentialOrderCodeGenerator _orderCodes = new();
    private readonly CheckoutHoldOrchestrator _holds;
    private readonly CheckoutPlanLine _cameraLine;
    private readonly CheckoutPlanLine _lightLine;
    private readonly CheckoutPlanLine _tripodLine;
    private readonly CheckoutPlanLine _unselectedLine;
    private readonly CheckoutPlan _plan;

    public CheckoutOrderWriterTests()
    {
        _holds = new CheckoutHoldOrchestrator(_inventory, NullLogger<CheckoutHoldOrchestrator>.Instance);

        RentalCart cart = RentalCart.Create(_cartId, _renterId, Now).Value;
        _cameraLine = AddCartLine(cart, 2);
        _lightLine = AddCartLine(cart, 1);
        _tripodLine = AddCartLine(cart, 1);
        _unselectedLine = AddCartLine(cart, 1);

        using RentalServiceDbContext context = NewContext();
        context.RentalCarts.Add(cart);
        context.SaveChanges();

        _plan = Plan(_cameraLine, _lightLine, _tripodLine);
    }

    private static CheckoutPlanLine AddCartLine(RentalCart cart, int quantity)
    {
        Guid listingId = Guid.NewGuid();
        Guid supplierId = Guid.NewGuid();
        Guid cartItemId = cart.AddItem(Guid.NewGuid(), listingId, supplierId, quantity, Start, End, Now).Value.Id;

        return new(
            cartItemId,
            listingId,
            supplierId,
            quantity,
            Start,
            End,
            "Sony A7 IV",
            TestListingSnapshots.Default,
            500_000m,
            2_000_000m,
            3,
            500_000m * quantity * 3,
            2_000_000m * quantity);
    }

    private CheckoutPlan Plan(params CheckoutPlanLine[] lines) => new(
        _renterId,
        _cartId,
        new("Hồ Chí Minh", "123 Nguyễn Huệ, Quận 1", "Nguyễn Văn A", "0901234567", "Gọi trước khi giao"),
        new("VCB", "0123456789", "NGUYỄN VĂN A"),
        "v1.0",
        "terms/v1.0.pdf",
        "hash123456",
        "VND",
        lines);

    private RentalServiceDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalServiceDbContext>().UseInMemoryDatabase(_dbName).Options);

    private CheckoutOrderWriter CreateWriter(
        RentalServiceDbContext context,
        IUnitOfWork? unitOfWork = null,
        IOrderCodeGenerator? orderCodes = null,
        IRentalOrderRepository? orders = null) => new(
        orders ?? new RentalOrderRepository(context),
        new RentalCartRepository(context),
        orderCodes ?? _orderCodes,
        _holds,
        unitOfWork ?? context,
        NullLogger<CheckoutOrderWriter>.Instance);

    private async Task<IReadOnlyList<CheckoutHeldLine>> HoldAsync(CheckoutPlan plan, string operationId = OperationId) =>
        (await _holds.HoldAllLinesAsync(plan, operationId, Now)).HeldLines;

    private async Task<int> CountOrdersAsync()
    {
        await using RentalServiceDbContext context = NewContext();
        return await context.RentalOrders.CountAsync();
    }

    private async Task<RentalCart> ReloadCartAsync()
    {
        await using RentalServiceDbContext context = NewContext();
        return await context.RentalCarts.Include(c => c.Items).SingleAsync(c => c.Id == _cartId);
    }

    private async Task<Guid> SeedExistingOrderAsync(Guid renterId)
    {
        RentalOrder existing = RentalOrder.Create(
            Guid.NewGuid(),
            "DH-2026-999999",
            renterId,
            "VND",
            "Delivery",
            "Nguyễn Văn A",
            "0901234567",
            "123 Nguyễn Huệ, Quận 1, Hồ Chí Minh",
            null,
            null,
            "v1.0",
            "terms/v1.0.pdf",
            "hash123456",
            OperationId,
            Now,
            [
                new InitialRentalItemDto(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    1,
                    Start,
                    End,
                    "Lens",
                    null,
                    "Pickup",
                    100_000m,
                    200_000m,
                    "grp-existing",
                    [new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
                    TestListingSnapshots.Default)
            ]).Value;

        await using RentalServiceDbContext context = NewContext();
        context.RentalOrders.Add(existing);
        await context.SaveChangesAsync();
        return existing.Id;
    }

    [Fact]
    public async Task CreateOrder_PersistsOrderWithDeliveryTermsAndPaymentDeadline()
    {
        CheckoutHoldResult held = await _holds.HoldAllLinesAsync(_plan, OperationId, Now);

        await using RentalServiceDbContext writeContext = NewContext();
        Result<RentalOrder> result = await CreateWriter(writeContext).CreateOrderAsync(_plan, held.HeldLines, OperationId, Now);

        Assert.True(result.IsSuccess);
        await using RentalServiceDbContext readContext = NewContext();
        RentalOrder order = (await new RentalOrderRepository(readContext).GetByCheckoutOperationIdAsync(OperationId))!;

        Assert.Equal(result.Value.Id, order.Id);
        Assert.Equal("DH-2026-000001", order.OrderCode);
        Assert.Equal(_renterId, order.RenterId);
        Assert.Equal(RentalOrderStatus.PendingPayment, order.Status);
        Assert.Equal("VND", order.Currency);
        Assert.Equal(held.HoldExpiresAt, order.PaymentDeadline);
        Assert.Equal(OperationId, order.CheckoutOperationId);

        Assert.Equal("Delivery", order.DeliveryMethod);
        Assert.Equal("Nguyễn Văn A", order.RecipientName);
        Assert.Equal("0901234567", order.RecipientPhone);
        Assert.Equal("123 Nguyễn Huệ, Quận 1, Hồ Chí Minh", order.DeliveryAddress);
        Assert.Equal("Gọi trước khi giao", order.DeliveryNote);

        Assert.Equal("v1.0", order.TermsVersion);
        Assert.Equal("terms/v1.0.pdf", order.TermsDocumentRef);
        Assert.Equal("hash123456", order.TermsContentHash);
        Assert.NotNull(order.TermsAcceptance);
        Assert.Equal(_renterId, order.TermsAcceptance.RenterId);
        Assert.Equal("v1.0", order.TermsAcceptance.TermsVersion);
        Assert.Equal(Now, order.TermsAcceptance.AcceptedAt);

        Assert.Empty(_inventory.Releases);
    }

    [Fact]
    public async Task CreateOrder_StoresRefundAccountWithoutEscapingVietnameseCharacters()
    {
        IReadOnlyList<CheckoutHeldLine> held = await HoldAsync(_plan);

        await using RentalServiceDbContext context = NewContext();
        RentalOrder order = (await CreateWriter(context).CreateOrderAsync(_plan, held, OperationId, Now)).Value;

        Assert.Contains("NGUYỄN VĂN A", order.RefundDestinationRef, StringComparison.Ordinal);
        using JsonDocument refund = JsonDocument.Parse(order.RefundDestinationRef);
        Assert.Equal("VCB", refund.RootElement.GetProperty("bankCode").GetString());
        Assert.Equal("0123456789", refund.RootElement.GetProperty("accountNumber").GetString());
        Assert.Equal("NGUYỄN VĂN A", refund.RootElement.GetProperty("accountHolderName").GetString());
    }

    [Fact]
    public async Task CreateOrder_PersistsRentalItemsAndUnitAssignmentsFromHolds()
    {
        IReadOnlyList<CheckoutHeldLine> held = await HoldAsync(_plan);

        await using RentalServiceDbContext writeContext = NewContext();
        await CreateWriter(writeContext).CreateOrderAsync(_plan, held, OperationId, Now);

        await using RentalServiceDbContext readContext = NewContext();
        RentalOrder order = (await new RentalOrderRepository(readContext).GetByCheckoutOperationIdAsync(OperationId))!;

        Assert.Equal(3, order.Items.Count);
        Assert.Equal(4, order.Items.Sum(i => i.UnitAssignments.Count));

        foreach (CheckoutHeldLine heldLine in held)
        {
            RentalItem item = order.Items.Single(i => i.Id == heldLine.RentalItemId);
            CheckoutPlanLine line = heldLine.Line;

            Assert.Equal(heldLine.AllocationGroupId, item.AllocationGroupId);
            Assert.Equal(line.EquipmentListingId, item.EquipmentListingId);
            Assert.Equal(line.SupplierId, item.SupplierId);
            Assert.Equal(line.Quantity, item.Quantity);
            Assert.Equal(line.ExpectedStartDate, item.ExpectedStartDate);
            Assert.Equal(line.ExpectedEndDate, item.ExpectedEndDate);
            Assert.Equal(line.ListingName, item.ListingTitleSnapshot);
            // Inventory không trả nơi nhận máy nên đơn để trống.
            Assert.Null(item.PickupLocationSnapshot);
            Assert.Equal(line.DailyRate, item.DailyRateSnapshot);
            Assert.Equal(line.DepositPerUnit, item.DepositPerUnitSnapshot);
            Assert.Equal(line.RentalAmount, item.RentalAmount);
            Assert.Equal(line.DepositAmount, item.DepositAmount);
            Assert.Equal(line.ListingSnapshot.EquipmentTypeName, item.EquipmentTypeNameSnapshot);
            Assert.Equal(line.ListingSnapshot.SpecValuesJson, item.SpecValuesSnapshot);
            Assert.Equal(RentalItemStatus.PendingPayment, item.Status);

            Assert.Equal(
                heldLine.AssignedAssets.Select(a => (a.AssetId, a.AllocationId)).Order(),
                item.UnitAssignments.Select(u => (u.AssetId, u.AllocationId)).Order());
            Assert.All(item.UnitAssignments, u => Assert.Equal(RentalUnitAssignmentStatus.Assigned, u.Status));
        }
    }

    [Fact]
    public async Task CreateOrder_RemovesCheckedOutLinesFromCart_AndKeepsTheRest()
    {
        IReadOnlyList<CheckoutHeldLine> held = await HoldAsync(_plan);

        await using RentalServiceDbContext context = NewContext();
        await CreateWriter(context).CreateOrderAsync(_plan, held, OperationId, Now);

        RentalCart cart = await ReloadCartAsync();
        Assert.Equal(RentalCartStatus.Active, cart.Status);
        Assert.Equal(_unselectedLine.CartItemId, Assert.Single(cart.Items).Id);
    }

    [Fact]
    public async Task CreateOrder_MarksCartCheckedOut_WhenEveryLineIsCheckedOut()
    {
        CheckoutPlan fullPlan = Plan(_cameraLine, _lightLine, _tripodLine, _unselectedLine);
        IReadOnlyList<CheckoutHeldLine> held = await HoldAsync(fullPlan);

        await using RentalServiceDbContext context = NewContext();
        Result<RentalOrder> result = await CreateWriter(context).CreateOrderAsync(fullPlan, held, OperationId, Now);

        Assert.True(result.IsSuccess);
        RentalCart cart = await ReloadCartAsync();
        Assert.Equal(RentalCartStatus.CheckedOut, cart.Status);
        Assert.Empty(cart.Items);
    }

    [Fact]
    public async Task CreateOrder_ReleasesHoldsAndSavesNothing_WhenDomainRejectsTheOrder()
    {
        string tooLongOperationId = new('k', 65);
        IReadOnlyList<CheckoutHeldLine> held = await HoldAsync(_plan, tooLongOperationId);

        await using RentalServiceDbContext context = NewContext();
        Result<RentalOrder> result = await CreateWriter(context).CreateOrderAsync(_plan, held, tooLongOperationId, Now);

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.InvalidSnapshot, result.Error);
        Assert.Equal(
            held.Select(h => h.AllocationGroupId).Reverse(),
            _inventory.Releases.Select(r => r.AllocationGroupId));
        Assert.Equal(0, await CountOrdersAsync());
        Assert.Equal(4, (await ReloadCartAsync()).Items.Count);
    }

    [Fact]
    public async Task CreateOrder_ReleasesHolds_WhenCartIsNoLongerTheOneThatWasValidated()
    {
        CheckoutPlan stalePlan = _plan with { CartId = Guid.NewGuid() };
        IReadOnlyList<CheckoutHeldLine> held = await HoldAsync(stalePlan);

        await using RentalServiceDbContext context = NewContext();
        Result<RentalOrder> result = await CreateWriter(context).CreateOrderAsync(stalePlan, held, OperationId, Now);

        Assert.Equal(RentalErrors.CartNotFound, result.Error);
        Assert.Equal(3, _inventory.Releases.Count);
        Assert.Equal(0, _orderCodes.IssuedCount);
        Assert.Equal(0, await CountOrdersAsync());
    }

    [Fact]
    public async Task CreateOrder_ReleasesHoldsAndLeavesCartUntouched_WhenSaveFails()
    {
        IReadOnlyList<CheckoutHeldLine> held = await HoldAsync(_plan);

        await using RentalServiceDbContext context = NewContext();
        Result<RentalOrder> result = await CreateWriter(context, new FailingUnitOfWork(context))
            .CreateOrderAsync(_plan, held, OperationId, Now);

        Assert.Equal(CheckoutErrors.OrderNotSaved, result.Error);
        Assert.Equal(3, _inventory.Releases.Count);
        Assert.Equal(0, await CountOrdersAsync());
        Assert.Equal(4, (await ReloadCartAsync()).Items.Count);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task CreateOrder_ReturnsExistingOrderWithoutReleasing_WhenSameCheckoutWasAlreadySaved()
    {
        Guid existingOrderId = await SeedExistingOrderAsync(_renterId);
        IReadOnlyList<CheckoutHeldLine> held = await HoldAsync(_plan);

        await using RentalServiceDbContext context = NewContext();
        Result<RentalOrder> result = await CreateWriter(context, new FailingUnitOfWork(context))
            .CreateOrderAsync(_plan, held, OperationId, Now);

        Assert.True(result.IsSuccess);
        Assert.Equal(existingOrderId, result.Value.Id);
        Assert.Empty(_inventory.Releases);
        Assert.Equal(1, await CountOrdersAsync());
    }

    [Fact]
    public async Task CreateOrder_DoesNotReturnAnotherRentersOrder_WhenOperationIdCollides()
    {
        await SeedExistingOrderAsync(Guid.NewGuid());
        IReadOnlyList<CheckoutHeldLine> held = await HoldAsync(_plan);

        await using RentalServiceDbContext context = NewContext();
        Result<RentalOrder> result = await CreateWriter(context, new FailingUnitOfWork(context))
            .CreateOrderAsync(_plan, held, OperationId, Now);

        Assert.Equal(CheckoutErrors.OrderNotSaved, result.Error);
        Assert.Equal(3, _inventory.Releases.Count);
    }

    [Fact]
    public async Task CreateOrder_ReleasesHoldsAndRethrows_WhenSavingFailsUnexpectedly()
    {
        IReadOnlyList<CheckoutHeldLine> held = await HoldAsync(_plan);

        await using RentalServiceDbContext context = NewContext();
        CheckoutOrderWriter writer = CreateWriter(
            context, new ThrowingUnitOfWork(context, () => new InvalidOperationException("Simulated database outage.")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.CreateOrderAsync(_plan, held, OperationId, Now));

        Assert.Equal(
            held.Select(h => h.AllocationGroupId).Reverse(),
            _inventory.Releases.Select(r => r.AllocationGroupId));
        Assert.Equal(0, await CountOrdersAsync());
        Assert.Equal(4, (await ReloadCartAsync()).Items.Count);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task CreateOrder_ReleasesHoldsWithoutTheRequestToken_WhenRenterDisconnectsWhileSaving()
    {
        IReadOnlyList<CheckoutHeldLine> held = await HoldAsync(_plan);
        using CancellationTokenSource requestAborted = new();

        await using RentalServiceDbContext context = NewContext();
        CheckoutOrderWriter writer = CreateWriter(context, new ThrowingUnitOfWork(context, () =>
        {
            requestAborted.Cancel();
            return new OperationCanceledException(requestAborted.Token);
        }));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => writer.CreateOrderAsync(_plan, held, OperationId, Now, requestAborted.Token));

        Assert.Equal(3, _inventory.Releases.Count);
        Assert.All(_inventory.Releases, r => Assert.Equal(CancellationToken.None, r.Token));
        Assert.Equal(0, await CountOrdersAsync());
    }

    [Fact]
    public async Task CreateOrder_ReleasesHoldsAndRethrows_WhenOrderCodeCannotBeIssued()
    {
        IReadOnlyList<CheckoutHeldLine> held = await HoldAsync(_plan);

        await using RentalServiceDbContext context = NewContext();
        CheckoutOrderWriter writer = CreateWriter(context, orderCodes: new FailingOrderCodeGenerator());

        await Assert.ThrowsAsync<TimeoutException>(() => writer.CreateOrderAsync(_plan, held, OperationId, Now));

        Assert.Equal(3, _inventory.Releases.Count);
        Assert.Equal(0, await CountOrdersAsync());
        Assert.Equal(4, (await ReloadCartAsync()).Items.Count);
    }

    [Fact]
    public async Task CreateOrder_KeepsHolds_WhenSaveReportsFailureButTheOrderWasCommitted()
    {
        IReadOnlyList<CheckoutHeldLine> held = await HoldAsync(_plan);

        await using RentalServiceDbContext context = NewContext();
        CheckoutOrderWriter writer = CreateWriter(context, new CommitThenThrowUnitOfWork(context));

        await Assert.ThrowsAsync<TimeoutException>(() => writer.CreateOrderAsync(_plan, held, OperationId, Now));

        // Đơn đã nằm trong database và đang dùng các hold này: nhả ra là có đơn không còn máy.
        Assert.Empty(_inventory.Releases);
        Assert.Equal(1, await CountOrdersAsync());
    }

    [Fact]
    public async Task CreateOrder_KeepsHolds_WhenItCannotConfirmWhetherTheOrderWasSaved()
    {
        IReadOnlyList<CheckoutHeldLine> held = await HoldAsync(_plan);

        // FakeOrderRepository ném lỗi ở mọi thao tác đọc ghi đơn, như khi database không truy cập được.
        await using RentalServiceDbContext context = NewContext();
        CheckoutOrderWriter writer = CreateWriter(context, orders: new FakeOrderRepository());

        await Assert.ThrowsAsync<NotSupportedException>(() => writer.CreateOrderAsync(_plan, held, OperationId, Now));

        Assert.Empty(_inventory.Releases);
    }

    [Fact]
    public void OrderDeliveryTermsAndAssignmentSnapshots_HaveNoPublicSetter()
    {
        string[] orderProperties =
        [
            nameof(RentalOrder.OrderCode),
            nameof(RentalOrder.RenterId),
            nameof(RentalOrder.Currency),
            nameof(RentalOrder.DeliveryMethod),
            nameof(RentalOrder.RecipientName),
            nameof(RentalOrder.RecipientPhone),
            nameof(RentalOrder.DeliveryAddress),
            nameof(RentalOrder.DeliveryNote),
            nameof(RentalOrder.RefundDestinationRef),
            nameof(RentalOrder.TermsVersion),
            nameof(RentalOrder.TermsDocumentRef),
            nameof(RentalOrder.TermsContentHash),
            nameof(RentalOrder.CheckoutOperationId)
        ];
        string[] assignmentProperties =
        [
            nameof(RentalUnitAssignment.RentalItemId),
            nameof(RentalUnitAssignment.AssetId),
            nameof(RentalUnitAssignment.AllocationId)
        ];

        Assert.All(orderProperties, name => AssertNoPublicSetter(typeof(RentalOrder), name));
        Assert.All(assignmentProperties, name => AssertNoPublicSetter(typeof(RentalUnitAssignment), name));
        AssertNoPublicSetter(typeof(RentalItem), nameof(RentalItem.AllocationGroupId));
    }

    private static void AssertNoPublicSetter(Type type, string propertyName)
    {
        PropertyInfo property = type.GetProperty(propertyName)!;
        Assert.False(property.SetMethod?.IsPublic ?? false, $"{type.Name}.{propertyName} không được có public setter.");
    }

    private sealed class SequentialOrderCodeGenerator : IOrderCodeGenerator
    {
        public int IssuedCount { get; private set; }

        public Task<string> NextAsync(DateTimeOffset utcNow, CancellationToken cancellationToken = default)
        {
            IssuedCount++;
            return Task.FromResult($"DH-{utcNow.Year}-{IssuedCount:D6}");
        }
    }

    private sealed class FailingOrderCodeGenerator : IOrderCodeGenerator
    {
        public Task<string> NextAsync(DateTimeOffset utcNow, CancellationToken cancellationToken = default) =>
            throw new TimeoutException("Simulated sequence timeout.");
    }

    /// <summary>Giả lập lần lưu hỏng vì lý do ngoài dự kiến: không có gì được ghi.</summary>
    private sealed class ThrowingUnitOfWork(RentalServiceDbContext inner, Func<Exception> failure) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => throw failure();

        public void DiscardChanges() => inner.DiscardChanges();
        public Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            inner.BeginTransactionAsync(cancellationToken);
    }

    /// <summary>Giả lập mất kết nối ngay sau khi database đã commit: dữ liệu đã ghi nhưng lời gọi báo lỗi.</summary>
    private sealed class CommitThenThrowUnitOfWork(RentalServiceDbContext inner) : IUnitOfWork
    {
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            await inner.SaveChangesAsync(cancellationToken);
            throw new TimeoutException("Simulated connection loss after commit.");
        }

        public void DiscardChanges() => inner.DiscardChanges();
        public Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            inner.BeginTransactionAsync(cancellationToken);
    }

    /// <summary>Giả lập database từ chối lần lưu (ví dụ trùng CheckoutOperationId).</summary>
    private sealed class FailingUnitOfWork(RentalServiceDbContext inner) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("Simulated unique violation.");

        public void DiscardChanges() => inner.DiscardChanges();
        public Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            inner.BeginTransactionAsync(cancellationToken);
    }
}
