using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Api.Tests.Orders;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Refunds;
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Orders;
using Dprental.RentalService.Application.Services.Refunds;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Refunds;

/// <summary>
/// Hoàn tiền bên Rental trên PostgreSQL thật: dòng bị từ chối sau khi thanh toán hoàn toàn bộ, dòng đã trả máy và chốt
/// khấu trừ hoàn phần cọc còn lại. Admin xem menu và chi tiết đơn với tài khoản đủ số, xác nhận chuyển khoản;
/// người thuê thấy đơn đã hoàn tiền.
/// </summary>
public sealed partial class RentalRefundPostgreSqlTests
{
    private static readonly DateTimeOffset ConfirmedNow = OrderTestData.Now.AddDays(10);

    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _an = Guid.NewGuid();
    private readonly Guid _dung = Guid.NewGuid();
    private readonly Guid _binh = Guid.NewGuid();
    private readonly Guid _cuong = Guid.NewGuid();

    [Fact]
    public async Task Queue_Pending_ListsFullRefundsAndDepositReturnsOldestFirst_WithFullRefundAccount()
    {
        await WithSeededDatabaseAsync(async (options, seed) =>
        {
            Result<PagedResponse<AdminRefundQueueItemResponse>> result =
                await QueryService(options).GetRefundsAsync("PENDING", 1, 20, Admin());

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);
            Assert.Equal(2, result.Value.TotalCount);
            Assert.Equal([seed.Excavator.Id, seed.Mixers.Id], result.Value.Items.Select(i => i.RentalItemId));

            AdminRefundQueueItemResponse excavator = result.Value.Items[0];
            Assert.Equal(RentalRefundKind.FullRefund, excavator.Kind);
            Assert.Equal(AdminRefundQueueState.Pending, excavator.State);
            Assert.Equal("DH-2026-000010", excavator.OrderCode);
            Assert.Equal(seed.LifecycleOrderId, excavator.OrderId);
            Assert.Equal("Nguyễn Văn An", excavator.RenterName);
            Assert.Equal("0901234567", excavator.RenterPhone);
            Assert.Equal("Máy xúc CAT 320", excavator.EquipmentName);
            Assert.Equal(RentalItemStatus.Rejected, excavator.ItemStatus);
            Assert.Equal(0m, excavator.DeductionAmount);
            Assert.Equal(seed.Excavator.RentalAmount + seed.Excavator.DepositAmount, excavator.RefundAmount);
            Assert.Equal(new RenterRefundAccountResponse("VCB", "0123456789", "NGUYỄN VĂN AN"), excavator.RefundAccount);
            Assert.Equal("Pending", excavator.PaymentRequestStatus);
            Assert.Null(excavator.Confirmation);

            AdminRefundQueueItemResponse mixers = result.Value.Items[1];
            Assert.Equal(RentalRefundKind.DepositReturn, mixers.Kind);
            Assert.Equal(RentalItemStatus.PendingSettlement, mixers.ItemStatus);
            Assert.Equal(6_000_000m, mixers.DepositAmount);
            Assert.Equal(1_500_000m, mixers.DeductionAmount);
            Assert.Equal(4_500_000m, mixers.RefundAmount);
        });
    }

    [Fact]
    public async Task Queue_CompletedAndAll_UseAdminConfirmations_AndSkipFullyDeductedOrNeverRequested()
    {
        await WithSeededDatabaseAsync(async (options, seed) =>
        {
            AdminRefundQueryService service = QueryService(options);

            Result<PagedResponse<AdminRefundQueueItemResponse>> completed = await service.GetRefundsAsync("COMPLETED", 1, 20, Admin());
            Assert.True(completed.IsSuccess);
            Assert.Equal([seed.RefundedItem.Id, seed.Rammer.Id], completed.Value.Items.Select(i => i.RentalItemId));
            AdminRefundQueueItemResponse rammer = completed.Value.Items[1];
            Assert.Equal(AdminRefundQueueState.Completed, rammer.State);
            Assert.NotNull(rammer.Confirmation);
            Assert.Equal("FT-RAMMER-001", rammer.Confirmation.BankTransactionRef);
            Assert.Equal(rammer.RefundAmount, rammer.Confirmation.RefundAmount);
            Assert.Equal(new RenterRefundAccountResponse("VCB", "0123456789", "NGUYỄN VĂN AN"), rammer.Confirmation.RefundAccount);

            Result<PagedResponse<AdminRefundQueueItemResponse>> all = await service.GetRefundsAsync(null, 1, 20, Admin());
            Assert.Equal(
                [seed.Mixers.Id, seed.RefundedItem.Id, seed.Rammer.Id, seed.Excavator.Id],
                all.Value.Items.Select(i => i.RentalItemId));

            Result<PagedResponse<AdminRefundQueueItemResponse>> firstPage = await service.GetRefundsAsync(null, 1, 3, Admin());
            Assert.Equal(4, firstPage.Value.TotalCount);
            Assert.Equal(2, firstPage.Value.TotalPages);
        });
    }

    [Fact]
    public async Task Queue_RejectsInvalidStatusAndNonAdminCallers()
    {
        await WithSeededDatabaseAsync(async (options, _) =>
        {
            AdminRefundQueryService service = QueryService(options);

            Assert.Equal("Order.InvalidStatus", (await service.GetRefundsAsync("DONE", 1, 20, Admin())).Error.Code);
            Assert.Equal("Auth.Forbidden", (await service.GetRefundsAsync(null, 1, 20, OrderTestData.User(_an))).Error.Code);
            Assert.Equal(
                "Auth.ServiceTokenNotAllowed",
                (await service.GetRefundsAsync(null, 1, 20, RentalCallerContext.ForService("payment", roles: ["admin"]))).Error.Code);
            Assert.Equal(ErrorType.Unauthorized, (await service.GetRefundsAsync(null, 1, 20, OrderTestData.Anonymous())).Error.Type);
        });
    }

    [Fact]
    public async Task ConfirmTransfer_RecordsConfirmation_MovesItToCompleted_AndNotifiesRenter()
    {
        await WithSeededDatabaseAsync(async (options, seed) =>
        {
            FakeOrderHubNotifier notifier = new();

            Result<AdminOrderRefundResponse> result = await ConfirmAsync(
                options, seed.Excavator.Id, new("  FT26301845123456 ", " Chuyển qua VCB "), Admin(), notifier);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);
            Assert.Equal(AdminRefundQueueState.Completed, result.Value.State);
            AdminRefundConfirmationResponse confirmation = Assert.IsType<AdminRefundConfirmationResponse>(result.Value.Confirmation);
            Assert.Equal("FT26301845123456", confirmation.BankTransactionRef);
            Assert.Equal("Chuyển qua VCB", confirmation.Note);
            Assert.Equal(_admin, confirmation.ConfirmedBy);
            Assert.Equal(ConfirmedNow, confirmation.ConfirmedAt);
            Assert.Equal(seed.Excavator.RentalAmount + seed.Excavator.DepositAmount, confirmation.RefundAmount);
            Assert.Equal(new RenterRefundAccountResponse("VCB", "0123456789", "NGUYỄN VĂN AN"), confirmation.RefundAccount);

            var note = Assert.Single(notifier.RenterNotifications);
            Assert.Equal(_an, note.RenterId);
            Assert.Equal(seed.LifecycleOrderId, note.Notification.OrderId);
            Assert.Equal(OrderChangeTypes.RefundChanged, note.Notification.ChangeType);
            Assert.Empty(notifier.SupplierNotifications);

            await using RentalServiceDbContext verify = new(options);
            RentalRefundConfirmation stored = await verify.RentalRefundConfirmations
                .AsNoTracking()
                .SingleAsync(c => c.RentalItemId == seed.Excavator.Id);
            Assert.Equal(RentalRefundKind.FullRefund, stored.Kind);
            Assert.Equal("0123456789", stored.AccountNumber);
            RentalOrder order = await verify.RentalOrders.AsNoTracking().SingleAsync(o => o.Id == seed.LifecycleOrderId);
            Assert.Equal(note.Notification.Version, order.Version);

            Result<PagedResponse<AdminRefundQueueItemResponse>> pending =
                await QueryService(options).GetRefundsAsync("PENDING", 1, 20, Admin());
            Assert.Equal([seed.Mixers.Id], pending.Value.Items.Select(i => i.RentalItemId));
        });
    }

    [Fact]
    public async Task ConfirmTransfer_SameRefReturnsStoredResult_OtherRefOrReusedRefIsRejected()
    {
        await WithSeededDatabaseAsync(async (options, seed) =>
        {
            Result<AdminOrderRefundResponse> first = await ConfirmAsync(options, seed.Excavator.Id, new("FT-EXC-001", null), Admin());
            Result<AdminOrderRefundResponse> replay = await ConfirmAsync(options, seed.Excavator.Id, new("FT-EXC-001", null), Admin());
            Result<AdminOrderRefundResponse> otherRef = await ConfirmAsync(options, seed.Excavator.Id, new("FT-EXC-002", null), Admin());
            Result<AdminOrderRefundResponse> reusedRef = await ConfirmAsync(options, seed.Mixers.Id, new("FT-RAMMER-001", null), Admin());

            Assert.True(first.IsSuccess);
            Assert.True(replay.IsSuccess);
            Assert.Equal(first.Value.Confirmation, replay.Value.Confirmation);
            Assert.Equal("Refund.AlreadyConfirmed", otherRef.Error.Code);
            Assert.Equal("Refund.DuplicateBankTransactionRef", reusedRef.Error.Code);

            await using RentalServiceDbContext verify = new(options);
            Assert.Equal(1, await verify.RentalRefundConfirmations.CountAsync(c => c.RentalItemId == seed.Excavator.Id));
            Assert.False(await verify.RentalRefundConfirmations.AnyAsync(c => c.RentalItemId == seed.Mixers.Id));
        });
    }

    [Fact]
    public async Task ConfirmTransfer_RejectsInvalidInput_ItemsWithoutRefund_AndNonAdminCallers()
    {
        await WithSeededDatabaseAsync(async (options, seed) =>
        {
            Assert.Equal("Refund.InvalidRequest", (await ConfirmAsync(options, seed.Excavator.Id, new("  ", null), Admin())).Error.Code);
            Assert.Equal("Refund.InvalidRequest", (await ConfirmAsync(options, seed.Excavator.Id, null, Admin())).Error.Code);
            Assert.Equal(
                "Refund.InvalidRequest",
                (await ConfirmAsync(options, seed.Excavator.Id, new(new string('A', 65), null), Admin())).Error.Code);
            Assert.Equal(
                "Refund.InvalidRequest",
                (await ConfirmAsync(options, seed.Excavator.Id, new("FT-1", new string('x', 501)), Admin())).Error.Code);

            Assert.Equal("Refund.NotFound", (await ConfirmAsync(options, seed.UnrequestedItem.Id, new("FT-2", null), Admin())).Error.Code);
            Assert.Equal("Refund.NotFound", (await ConfirmAsync(options, seed.FullyDeductedItem.Id, new("FT-3", null), Admin())).Error.Code);
            Assert.Equal("Refund.NotFound", (await ConfirmAsync(options, Guid.NewGuid(), new("FT-4", null), Admin())).Error.Code);

            Assert.Equal("Auth.Forbidden", (await ConfirmAsync(options, seed.Excavator.Id, new("FT-5", null), OrderTestData.User(_an))).Error.Code);
            Assert.Equal(
                "Auth.ServiceTokenNotAllowed",
                (await ConfirmAsync(options, seed.Excavator.Id, new("FT-6", null), RentalCallerContext.ForService("payment", roles: ["admin"]))).Error.Code);

            await using RentalServiceDbContext verify = new(options);
            Assert.Equal(2, await verify.RentalRefundConfirmations.CountAsync());
        });
    }

    [Fact]
    public async Task AdminOrderDetail_HasRefundSectionWithFullAccountsAndConfirmations()
    {
        await WithSeededDatabaseAsync(async (options, seed) =>
        {
            await using RentalServiceDbContext context = new(options);
            Result<AdminOrderDetailResponse> result = await new AdminOrderQueryService(
                    new RentalOrderRepository(context), NullLogger<AdminOrderQueryService>.Instance)
                .GetOrderAsync(seed.LifecycleOrderId, Admin());

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);
            AdminOrderDetailResponse order = result.Value;
            Assert.Equal("******6789", order.RefundAccount!.MaskedAccountNumber);
            Assert.Equal([seed.Excavator.Id, seed.Rammer.Id, seed.Mixers.Id], order.Refunds.Select(r => r.RentalItemId));

            AdminOrderRefundResponse excavator = order.Refunds[0];
            Assert.Equal(AdminRefundQueueState.Pending, excavator.State);
            Assert.Equal("0123456789", excavator.RefundAccount!.AccountNumber);

            AdminOrderRefundResponse rammer = order.Refunds[1];
            Assert.Equal(AdminRefundQueueState.Completed, rammer.State);
            Assert.Equal("FT-RAMMER-001", rammer.Confirmation!.BankTransactionRef);

            AdminOrderRefundResponse mixers = order.Refunds[2];
            Assert.Equal(RentalRefundKind.DepositReturn, mixers.Kind);
            Assert.Equal(4_500_000m, mixers.RefundAmount);

            AdminOrderItemResponse rammerItem = order.SubOrders.SelectMany(s => s.Items).Single(i => i.RentalItemId == seed.Rammer.Id);
            Assert.Equal(OrderRefundState.Completed, rammerItem.Refund.State);
        });
    }

    [Fact]
    public async Task RenterOrders_ShowRefundState_AndFilterRefundedOrPending()
    {
        await WithSeededDatabaseAsync(async (options, seed) =>
        {
            await using RentalServiceDbContext context = new(options);
            RenterOrderQueryService service = new(new RentalOrderRepository(context));

            Result<PagedResponse<RenterOrderSummaryResponse>> all = await service.GetMyOrdersAsync(1, 20, null, OrderTestData.User(_an));
            Assert.True(all.IsSuccess);
            Assert.Equal([seed.RefundedOrderId, seed.LifecycleOrderId], all.Value.Items.Select(o => o.OrderId));

            RenterOrderSummaryResponse refundedOrder = all.Value.Items[0];
            Assert.Equal(OrderRefundProgress.Refunded, refundedOrder.RefundState);
            Assert.Equal(seed.RefundedItem.RentalAmount + seed.RefundedItem.DepositAmount, refundedOrder.RefundedAmount);

            RenterOrderSummaryResponse lifecycle = all.Value.Items[1];
            Assert.Equal(OrderRefundProgress.Pending, lifecycle.RefundState);
            Assert.Equal(seed.Rammer.RentalAmount + seed.Rammer.DepositAmount, lifecycle.RefundedAmount);

            Result<PagedResponse<RenterOrderSummaryResponse>> refunded =
                await service.GetMyOrdersAsync(1, 20, null, OrderTestData.User(_an), "REFUNDED");
            Assert.Equal([seed.RefundedOrderId], refunded.Value.Items.Select(o => o.OrderId));
            Assert.Equal(1, refunded.Value.TotalCount);

            Result<PagedResponse<RenterOrderSummaryResponse>> pending =
                await service.GetMyOrdersAsync(1, 20, null, OrderTestData.User(_an), "pending");
            Assert.Equal([seed.LifecycleOrderId], pending.Value.Items.Select(o => o.OrderId));

            // Đơn của Dũng: một dòng chưa có yêu cầu hoàn, một dòng bị khấu trừ hết cọc, nên không có khoản hoàn nào.
            Result<PagedResponse<RenterOrderSummaryResponse>> dung = await service.GetMyOrdersAsync(1, 20, null, OrderTestData.User(_dung));
            Assert.Equal(OrderRefundProgress.None, Assert.Single(dung.Value.Items).RefundState);

            Assert.Equal(
                "Order.InvalidRefundState",
                (await service.GetMyOrdersAsync(1, 20, null, OrderTestData.User(_an), "NONE")).Error.Code);
            Assert.Equal(
                "Order.InvalidRefundState",
                (await service.GetMyOrdersAsync(1, 20, null, OrderTestData.User(_an), "DONE")).Error.Code);
        });
    }

    [Fact]
    public async Task RenterOrderDetail_ShowsRefundOfEachItem_AndOrderRefundState()
    {
        await WithSeededDatabaseAsync(async (options, seed) =>
        {
            await using RentalServiceDbContext context = new(options);
            Result<RenterOrderDetailResponse> result = await new RenterOrderQueryService(new RentalOrderRepository(context))
                .GetMyOrderAsync(seed.LifecycleOrderId, OrderTestData.User(_an));

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);
            Assert.Equal(OrderRefundProgress.Pending, result.Value.RefundState);
            Assert.Equal(seed.Rammer.RentalAmount + seed.Rammer.DepositAmount, result.Value.RefundedAmount);

            List<RenterOrderItemResponse> items = result.Value.SubOrders.SelectMany(s => s.Items).ToList();
            OrderItemRefundResponse excavator = items.Single(i => i.RentalItemId == seed.Excavator.Id).Refund;
            Assert.Equal(OrderRefundState.Processing, excavator.State);
            Assert.Equal(RentalRefundKind.FullRefund, excavator.Kind);
            Assert.Equal(seed.Excavator.RentalAmount + seed.Excavator.DepositAmount, excavator.RefundAmount);
            Assert.Null(excavator.RefundedAt);

            OrderItemRefundResponse rammer = items.Single(i => i.RentalItemId == seed.Rammer.Id).Refund;
            Assert.Equal(OrderRefundState.Completed, rammer.State);
            Assert.Equal(OrderTestData.Now.AddDays(1), rammer.RefundedAt);

            OrderItemRefundResponse mixers = items.Single(i => i.RentalItemId == seed.Mixers.Id).Refund;
            Assert.Equal(OrderRefundState.Processing, mixers.State);
            Assert.Equal(RentalRefundKind.DepositReturn, mixers.Kind);
            Assert.Equal(4_500_000m, mixers.RefundAmount);
        });
    }

    private RentalCallerContext Admin() => RentalCallerContext.ForUser(_admin, roles: ["admin"]);

    private static AdminRefundQueryService QueryService(DbContextOptions<RentalServiceDbContext> options) =>
        new(new RentalRefundRepository(new RentalServiceDbContext(options)), NullLogger<AdminRefundQueryService>.Instance);

    private static async Task<Result<AdminOrderRefundResponse>> ConfirmAsync(
        DbContextOptions<RentalServiceDbContext> options,
        Guid rentalItemId,
        ConfirmRefundTransferRequest? request,
        RentalCallerContext caller,
        IOrderHubNotifier? notifier = null)
    {
        await using RentalServiceDbContext context = new(options);
        AdminRefundCommandService service = new(
            new RentalOrderRepository(context),
            new RentalRefundRepository(context),
            new FakeOrderLockManager(),
            context,
            new FixedTimeProvider(ConfirmedNow),
            NullLogger<AdminRefundCommandService>.Instance,
            new Dprental.RentalService.Api.Tests.Checkout.FakePaymentRentalClient
            {
                RefundSync = r => new Dprental.RentalService.Application.DTOs.Payment.RentalRefundSyncResponse(
                    true, r.Amount, r.BankTransactionRef, r.ConfirmedBy, r.ConfirmedAt)
            },
            notifier);
        return await service.ConfirmTransferAsync(rentalItemId, request, caller);
    }

    private sealed record SeededItems(
        Guid LifecycleOrderId,
        Guid RefundedOrderId,
        RentalItem Excavator,
        RentalItem Rammer,
        RentalItem Mixers,
        RentalItem RefundedItem,
        RentalItem UnrequestedItem,
        RentalItem FullyDeductedItem);

    private async Task WithSeededDatabaseAsync(Func<DbContextOptions<RentalServiceDbContext>, SeededItems, Task> test)
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available at test connection string.");
        }

        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            DbContextOptions<RentalServiceDbContext> options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            await using (RentalServiceDbContext migrate = new(options))
            {
                await migrate.Database.MigrateAsync();
            }

            await test(options, await SeedAsync(options));
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    private async Task<SeededItems> SeedAsync(DbContextOptions<RentalServiceDbContext> options)
    {
        DateTimeOffset now = OrderTestData.Now;

        // Đơn của An: Bình từ chối máy xúc (chờ hoàn) và máy đầm (Admin đã chuyển khoản);
        // Cường giao 2 máy trộn, khấu trừ 1,5 triệu cọc (chờ hoàn cọc).
        RentalOrder lifecycle = OrderTestData.Paid(
            OrderTestData.Order(
                _an,
                "DH-2026-000010",
                now,
                OrderTestData.Line(_binh, "Máy xúc CAT 320", "Máy xúc", "Caterpillar", 1, 3_000_000m, 20_000_000m),
                OrderTestData.Line(_binh, "Máy đầm cóc Mikasa", "Máy đầm", "Mikasa", 1, 300_000m, 2_000_000m),
                OrderTestData.Line(_cuong, "Máy trộn bê tông 350L", "Máy trộn", "Hòa Phát", 2, 400_000m, 3_000_000m)),
            now.AddMinutes(5));
        OrderTestData.LinkItemsToOrder(lifecycle);

        RentalItem excavator = lifecycle.Items.Single(i => i.ListingTitleSnapshot == "Máy xúc CAT 320");
        RentalItem rammer = lifecycle.Items.Single(i => i.ListingTitleSnapshot == "Máy đầm cóc Mikasa");
        RejectWithRefundRequest(lifecycle, excavator, now.AddHours(1));
        RejectWithRefundRequest(lifecycle, rammer, now.AddHours(2));
        Confirm(lifecycle, rammer, "FT-RAMMER-001", now.AddDays(1));

        RentalItem mixers = lifecycle.Items.Single(i => i.SupplierId == _cuong);
        ReturnWithAcceptedDeduction(lifecycle, mixers, _cuong, _an, 1_500_000m, now);

        // Đơn thứ hai của An: Bình từ chối, Admin đã chuyển khoản hoàn toàn bộ.
        RentalOrder refundedOrder = OrderTestData.Paid(
            OrderTestData.Order(
                _an,
                "DH-2026-000012",
                now.AddDays(2),
                OrderTestData.Line(_binh, "Máy đầm cóc Mikasa", "Máy đầm", "Mikasa", 1, 300_000m, 2_000_000m)),
            now.AddDays(2).AddMinutes(5));
        OrderTestData.LinkItemsToOrder(refundedOrder);
        RentalItem refundedItem = refundedOrder.Items.Single();
        RejectWithRefundRequest(refundedOrder, refundedItem, now.AddDays(2).AddHours(1));
        Confirm(refundedOrder, refundedItem, "FT-SECOND-001", now.AddDays(3));

        // Đơn của Dũng: Bình từ chối nhưng chưa có yêu cầu hoàn; máy trộn bị khấu trừ hết cọc nên không còn gì để hoàn.
        RentalOrder other = OrderTestData.Paid(
            OrderTestData.Order(
                _dung,
                "DH-2026-000011",
                now.AddDays(1),
                OrderTestData.Line(_binh, "Máy đầm cóc Mikasa", "Máy đầm", "Mikasa", 1, 300_000m, 2_000_000m),
                OrderTestData.Line(_cuong, "Máy trộn bê tông 350L", "Máy trộn", "Hòa Phát", 1, 400_000m, 3_000_000m)),
            now.AddDays(1).AddMinutes(5));
        OrderTestData.LinkItemsToOrder(other);
        RentalItem unrequested = other.Items.Single(i => i.SupplierId == _binh);
        unrequested.ApplySupplierReject(_binh, now.AddDays(1).AddHours(1), "Hết máy", null, "reject-other");
        RentalItem fullyDeducted = other.Items.Single(i => i.SupplierId == _cuong);
        ReturnWithAcceptedDeduction(other, fullyDeducted, _cuong, _dung, 3_000_000m, now.AddDays(1));

        await using RentalServiceDbContext context = new(options);
        context.RentalOrders.AddRange(lifecycle, refundedOrder, other);
        await context.SaveChangesAsync();

        return new SeededItems(lifecycle.Id, refundedOrder.Id, excavator, rammer, mixers, refundedItem, unrequested, fullyDeducted);
    }

    private void RejectWithRefundRequest(RentalOrder order, RentalItem item, DateTimeOffset at)
    {
        item.ApplySupplierReject(_binh, at, "Máy đang bảo trì", null, $"reject-{item.Id:N}");
        OrderTestData.RequestRefund(order, item, at);
    }

    private void Confirm(RentalOrder order, RentalItem item, string bankTransactionRef, DateTimeOffset at) =>
        order.AddRefundConfirmation(new RentalRefundConfirmation(
            Guid.NewGuid(),
            order.Id,
            item.Id,
            RentalRefundKind.FullRefund,
            item.RentalAmount + item.DepositAmount,
            "VCB",
            "0123456789",
            "NGUYỄN VĂN AN",
            bankTransactionRef,
            null,
            _admin,
            at));

    /// <summary>Giao máy, trả máy có hư hỏng, người thuê chấp nhận khấu trừ rồi Rental yêu cầu Payment quyết toán.</summary>
    private static void ReturnWithAcceptedDeduction(
        RentalOrder order,
        RentalItem item,
        Guid supplierId,
        Guid renterId,
        decimal deduction,
        DateTimeOffset orderedAt)
    {
        item.ApplySupplierAccept(supplierId, orderedAt.AddHours(1), $"CONF-{item.Id:N}", $"accept-{item.Id:N}");
        OrderTestData.HandOver(item, orderedAt.AddDays(3));

        List<RentalUnitAssignment> units = item.UnitAssignments.ToList();
        List<ReturnUnitData> returns = units
            .Select((u, index) => index == units.Count - 1
                ? new ReturnUnitData(u.Id, UnitCondition.Damaged, "Vỡ thùng trộn", [Guid.NewGuid()])
                : new ReturnUnitData(u.Id, UnitCondition.Normal, null, []))
            .ToList();
        ReturnTestDataSeeder.SeedReturn(
            item, supplierId, orderedAt.AddDays(6), null, returns, orderedAt.AddDays(6), supplierId);

        Result<DeductionProposal> proposal = item.SubmitDeduction(
            supplierId, deduction, "Vỡ thùng trộn", [Guid.NewGuid()], [units[^1].AssetId], orderedAt.AddDays(7));
        Assert.True(proposal.IsSuccess, proposal.IsFailure ? proposal.Error.Message : null);

        Result accepted = item.AcceptDeductionByRenter(renterId, orderedAt.AddDays(7).AddHours(1), $"accept-deduction-{item.Id:N}");
        Assert.True(accepted.IsSuccess, accepted.IsFailure ? accepted.Error.Message : null);

        order.AddOperationReceipt(new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            $"settle-{item.Id:N}",
            RentalItem.SettleRentalAction,
            "fingerprint",
            isSuccess: false,
            errorCode: null,
            errorMessage: null,
            createdAt: orderedAt.AddDays(8),
            status: RentalOperationReceipt.StatusPending,
            targetItemId: item.Id));
    }
}
