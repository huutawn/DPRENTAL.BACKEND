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

    public FakeInventoryListingClient Listings { get; } = new();

    public FakeInventoryRentalClient Inventory { get; } = new();

    public FakePaymentRentalClient Payment { get; } = new();

    public FakeOrderCodeGenerator OrderCodes { get; } = new();

    public FakeOrderLockManager Locks { get; } = new();

    public AdjustableTimeProvider Clock { get; } = new(Now);

    public RentalServiceDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalServiceDbContext>().UseInMemoryDatabase(_dbName).Options);

    /// <param name="units">Số máy của Listing; null thì Inventory giả giữ được không giới hạn.</param>
    public Guid AddListing(Guid supplierId, decimal dailyRate, int? units = null)
    {
        Guid listingId = Guid.NewGuid();
        Listings.Listings[listingId] = TestListings.Create(listingId, supplierId, "Thiết bị", dailyRate, thumbnailMediaId: Guid.NewGuid());
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

    /// <summary>
    /// Gửi đơn rồi chạy nốt phần bất đồng bộ (Inventory giữ máy, tạo đơn, tạo hóa đơn) như hệ thống thật.
    /// </summary>
    public async Task<CheckoutResult> SubmitAsync(
        Guid renterId,
        string key,
        CheckoutRequest request,
        CancellationToken cancellationToken = default)
    {
        CheckoutSubmitResult accepted = await AcceptAsync(renterId, key, request, cancellationToken);
        if (accepted.IsFailure)
        {
            return accepted.Failure!;
        }

        return await Completion.CompleteAsync(renterId, key, cancellationToken);
    }

    /// <summary>Chạy phần bất đồng bộ của checkout: Inventory giả, dispatcher và worker tạo hóa đơn dùng chung cho mọi renter.</summary>
    public CheckoutCompletion Completion => _completion ??= new CheckoutCompletion(
        NewContext, Clock, Inventory, Payment, Locks, _ => OrderCodes);

    private CheckoutCompletion? _completion;

    /// <summary>Chỉ bước API nhận đơn: kiểm tra, ghi lệnh giữ lịch vào Outbox, trả 202.</summary>
    public async Task<CheckoutSubmitResult> AcceptAsync(
        Guid renterId,
        string key,
        CheckoutRequest request,
        CancellationToken cancellationToken = default)
    {
        await using RentalServiceDbContext context = NewContext();
        RentalOrderRepository orders = new(context);
        RentalCartRepository carts = new(context);
        RentalCheckoutOperationRepository operations = new(context);
        OutboxRepository outbox = new(context);
        InventoryCommandProducer inventoryCommands = new(outbox, Microsoft.Extensions.Options.Options.Create(new KafkaOptions { Enabled = true }), Clock);

        CheckoutService service = new(
            new CheckoutPreparationService(
                carts,
                orders,
                Listings,
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

        return await service.SubmitAsync(new(renterId, true), request, key, cancellationToken);
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

        int expired = await expiry.ProcessPaymentTimeoutsAsync();
        await Completion.RunInventoryAsync(Guid.Empty);
        return expired;
    }

    /// <summary>Các việc OperationRecoveryWorker sẽ nhặt lên ở lượt quét kế tiếp.</summary>
    public async Task<IReadOnlyList<OperationReceiptDescriptor>> LoadPendingOperationsAsync()
    {
        await using RentalServiceDbContext context = NewContext();
        return await new OperationReceiptRepository(context)
            .GetPendingOperationDescriptorsAsync(Clock.GetUtcNow(), 50, excludedActions: [], unlimitedAttemptActions: ["CreateInvoice"]);
    }

    public async Task<List<RentalOrder>> LoadOrdersAsync()
    {
        await using RentalServiceDbContext context = NewContext();
        return await context.RentalOrders
            .Include(o => o.Items).ThenInclude(i => i.UnitAssignments)
            .Include(o => o.SubOrders)
            .OrderBy(o => o.OrderCode)
            .ToListAsync();
    }

    public async Task<int> CountCartItemsAsync(Guid renterId)
    {
        await using RentalServiceDbContext context = NewContext();
        return await context.RentalCartItems.CountAsync(i => i.RentalCart!.RenterId == renterId);
    }
}
