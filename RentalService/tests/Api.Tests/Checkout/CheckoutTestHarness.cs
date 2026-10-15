using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.IServices.Expiry;
using Dprental.RentalService.Application.Services.Expiry;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Services.Checkout;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Api.Tests.Checkout;

/// <summary>
/// Dựng cả luồng checkout thật (chuẩn bị, giữ lịch, lưu đơn, hóa đơn) và luồng hết hạn thanh toán trên EF InMemory,
/// với Inventory và Payment giả dùng chung cho mọi renter. Mỗi lần gửi dùng một DbContext mới như một HTTP request.
/// </summary>
internal sealed class CheckoutTestHarness
{
    // 10:00 ngày 09/10/2026 giờ Việt Nam.
    public static readonly DateTimeOffset Now = new(2026, 10, 9, 3, 0, 0, TimeSpan.Zero);
    public static readonly DateOnly Start = new(2026, 10, 12);
    // Giỏ lưu ngày trả có tính: giao 12/10, trả 14/10 là 3 ngày.
    public static readonly DateOnly End = new(2026, 10, 14);
    public const int RentalDays = 3;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _dbName = $"CheckoutHarness_{Guid.NewGuid():N}";
    private readonly Guid _equipmentTypeId = Guid.NewGuid();
    private readonly Guid _brandId = Guid.NewGuid();

    public CheckoutTestHarness()
    {
        Catalog.EquipmentTypeNames[_equipmentTypeId] = "Máy ảnh";
        Catalog.BrandNames[_brandId] = "Sony";
    }

    public FakeInventoryListingClient Listings { get; } = new();

    public FakeCatalogReferenceClient Catalog { get; } = new();

    public FakeInventoryRentalClient Inventory { get; } = new();

    public FakePaymentRentalClient Payment { get; } = new();

    public FakeOrderCodeGenerator OrderCodes { get; } = new();

    public FakeOrderLockManager Locks { get; } = new();

    public AdjustableTimeProvider Clock { get; } = new(Now);

    public RentalServiceDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalServiceDbContext>().UseInMemoryDatabase(_dbName).Options);

    /// <param name="units">Số máy của Listing; null thì Inventory giả giữ được không giới hạn.</param>
    public Guid AddListing(Guid supplierId, decimal dailyRate, decimal depositPerUnit, int? units = null)
    {
        Guid listingId = Guid.NewGuid();
        Listings.Listings[listingId] = new(
            listingId,
            supplierId,
            true,
            "Thiết bị",
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
        Listings.AvailableQuantities[listingId] = units ?? 10;
        if (units is int capacity)
        {
            Inventory.Capacities[listingId] = capacity;
        }

        return listingId;
    }

    /// <summary>Tạo giỏ cho renter, mỗi dòng thuê từ <see cref="Start"/> đến <see cref="End"/>. Trả về mã dòng giỏ theo thứ tự.</summary>
    public async Task<Guid[]> SeedCartAsync(Guid renterId, params (Guid ListingId, int Quantity)[] lines)
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), renterId, Now).Value;
        Guid[] cartItemIds = lines
            .Select(l => cart.AddItem(
                Guid.NewGuid(), l.ListingId, Listings.Listings[l.ListingId].OwnerUserId, l.Quantity, Start, End, Now).Value.Id)
            .ToArray();

        await using RentalServiceDbContext context = NewContext();
        context.RentalCarts.Add(cart);
        await context.SaveChangesAsync();
        return cartItemIds;
    }

    public static CheckoutRequest Request(decimal expectedTotal, params Guid[] cartItemIds) => new(
        cartItemIds,
        new("Hồ Chí Minh", "123 Nguyễn Huệ, Quận 1", "Nguyễn Văn A", "0901234567", null),
        new("VCB", "0123456789", "NGUYEN VAN A"),
        "v1.0",
        true,
        expectedTotal);

    /// <param name="writerUnitOfWork">Thay unit of work của bước lưu đơn để giả lập lỗi database.</param>
    public async Task<CheckoutResult> SubmitAsync(
        Guid renterId,
        string key,
        CheckoutRequest request,
        Func<RentalServiceDbContext, IUnitOfWork>? writerUnitOfWork = null,
        CancellationToken cancellationToken = default)
    {
        await using RentalServiceDbContext context = NewContext();
        RentalOrderRepository orders = new(context);
        RentalCartRepository carts = new(context);
        RentalCheckoutOperationRepository operations = new(context);
        OutboxRepository outbox = new(context);
        InventoryCommandProducer inventoryCommands = new(outbox, Microsoft.Extensions.Options.Options.Create(new KafkaOptions { Enabled = true }), Clock);
        CheckoutHoldOrchestrator holds = new(Inventory, NullLogger<CheckoutHoldOrchestrator>.Instance);
        CheckoutOrderWriter writer = new(orders, carts, OrderCodes, holds,
            writerUnitOfWork?.Invoke(context) ?? context, NullLogger<CheckoutOrderWriter>.Instance);

        CheckoutService service = new(
            new CheckoutPreparationService(
                carts,
                orders,
                Listings,
                Catalog,
                Microsoft.Extensions.Options.Options.Create(new RentalTermsOptions
                {
                    CurrentVersion = "v1.0",
                    DocumentRef = "terms/v1.0.pdf",
                    ContentHash = "hash123456"
                }),
                Clock),
            operations,
            orders,
            Locks,
            inventoryCommands,
            context,
            Clock);

        var accepted = await service.SubmitAsync(new(renterId, true), request, key, cancellationToken);
        if (accepted.IsFailure)
        {
            return CheckoutResult.Failure(accepted.Error);
        }

        string operationId = CheckoutRules.DeriveOperationId(renterId, key);
        var op = await operations.GetByOperationIdAsync(operationId, cancellationToken);
        if (op is null) return CheckoutResult.Failure(CheckoutErrors.OrderNotSaved);

        if (op.Status == CheckoutOperationStatus.Succeeded)
        {
            var existingOrder = await orders.GetByIdAsync(op.ExpectedRentalOrderId, cancellationToken);
            return CheckoutResult.Success(existingOrder!, isReplay: true);
        }

        var plan = JsonSerializer.Deserialize<CheckoutPlan>(op.PreparationSnapshotJson, JsonOptions)!;
        var held = await holds.HoldAllLinesAsync(plan, op.OperationId, Clock.GetUtcNow(), cancellationToken);
        if (!held.IsSuccess)
        {
            op.MarkFailed(held.Error.Code, held.Error.Message, Clock.GetUtcNow());
            await context.SaveChangesAsync(cancellationToken);
            return CheckoutResult.From(held);
        }

        Dprental.RentalService.Common.Result<RentalOrder> orderResult;
        try
        {
            orderResult = await writer.CreateOrderAsync(plan, held.HeldLines, op.OperationId,
                op.ExpectedRentalOrderId, op.HoldExpiresAt, Clock.GetUtcNow(), cancellationToken);
        }
        catch
        {
            await using var recoveryContext = NewContext();
            var recoveryOp = await new RentalCheckoutOperationRepository(recoveryContext)
                .GetByOperationIdAsync(op.OperationId, cancellationToken);
            if (recoveryOp is not null)
            {
                recoveryOp.MarkFailed(CheckoutErrors.OrderNotSaved.Code,
                    CheckoutErrors.OrderNotSaved.Message, Clock.GetUtcNow());
                await recoveryContext.SaveChangesAsync(cancellationToken);
            }
            throw;
        }
        if (orderResult.IsFailure)
        {
            op.MarkFailed(orderResult.Error.Code, orderResult.Error.Message, Clock.GetUtcNow());
            await context.SaveChangesAsync(cancellationToken);
            return CheckoutResult.Failure(orderResult.Error);
        }

        var order = orderResult.Value;
        var issuer = new CheckoutInvoiceIssuer(Payment, orders, context, Clock, NullLogger<CheckoutInvoiceIssuer>.Instance);
        var invoiceResult = await issuer.EnsureInvoiceAsync(order, cancellationToken);
        if (invoiceResult.IsFailure)
        {
            op.MarkSucceeded(Clock.GetUtcNow(), invoiceStatus: "Failed");
            await context.SaveChangesAsync(cancellationToken);
            return CheckoutResult.OrderFailure(invoiceResult.Error, order);
        }

        op.MarkSucceeded(Clock.GetUtcNow(), invoiceStatus: "Ready");
        await context.SaveChangesAsync(cancellationToken);

        return CheckoutResult.Success(order, isReplay: false);
    }

    /// <summary>Chạy một lượt quét hết hạn thanh toán như ExpiryScannerWorker, dùng chung Inventory và Payment giả.</summary>
    public async Task<int> RunPaymentExpiryScanAsync()
    {
        await using RentalServiceDbContext context = NewContext();
        RentalOrderRepository orders = new(context);
        OutboxRepository outbox = new(context);
        IConfiguration configuration = new ConfigurationBuilder().Build();

        RentalOperationExecutor executor = new(
            orders,
            new OperationReceiptRepository(context),
            outbox,
            Locks,
            Inventory,
            Payment,
            context,
            configuration,
            NullLogger<RentalOperationExecutor>.Instance,
            Clock);
        ExpiryWorkflowService expiry = new(
            orders, outbox, Locks, executor, context, configuration, NullLogger<ExpiryWorkflowService>.Instance, Clock);

        return await expiry.ProcessPaymentTimeoutsAsync();
    }

    /// <summary>Các việc OperationRecoveryWorker sẽ nhặt lên ở lượt quét kế tiếp.</summary>
    public async Task<IReadOnlyList<OperationReceiptDescriptor>> LoadPendingOperationsAsync()
    {
        await using RentalServiceDbContext context = NewContext();
        return await new OperationReceiptRepository(context)
            .GetPendingOperationDescriptorsAsync(Clock.GetUtcNow(), 50);
    }

    public async Task<List<RentalOrder>> LoadOrdersAsync()
    {
        await using RentalServiceDbContext context = NewContext();
        return await context.RentalOrders
            .Include(o => o.Items).ThenInclude(i => i.UnitAssignments)
            .OrderBy(o => o.OrderCode)
            .ToListAsync();
    }

    public async Task<int> CountCartItemsAsync(Guid renterId)
    {
        await using RentalServiceDbContext context = NewContext();
        return await context.RentalCartItems.CountAsync(i => i.RentalCart!.RenterId == renterId);
    }
}
