using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Services.Checkout;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Dprental.RentalService.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Checkout;

/// <summary>
/// Chạy checkout trên PostgreSQL thật: sequence mã đơn, advisory lock theo renter, ràng buộc unique
/// của CheckoutOperationId và concurrency token của đơn.
/// </summary>
public sealed class CheckoutServicePostgreSqlTests
{
    // 10:00 ngày 09/10/2026 giờ Việt Nam.
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 3, 0, 0, TimeSpan.Zero);
    private const string Key = "checkout-key-1";

    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _supplierId = Guid.NewGuid();
    private readonly Guid _listingId = Guid.NewGuid();
    private readonly Guid _equipmentTypeId = Guid.NewGuid();
    private readonly Guid _brandId = Guid.NewGuid();
    private readonly FakeInventoryListingClient _listings = new();
    private readonly FakeCatalogReferenceClient _catalog = new();
    private readonly FakeInventoryRentalClient _inventory = new();
    private readonly FakePaymentRentalClient _payment = new();

    [Fact]
    public async Task Submit_ConcurrentAndRepeatedSameKey_CreatesOnePayableOrder()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available at test connection string.");
        }

        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            DbContextOptions<RentalServiceDbContext> options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            Guid cartItemId = await SeedAsync(options);
            PostgresOrderLockManager locks = new(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = connectionString })
                    .Build(),
                NullLogger<PostgresOrderLockManager>.Instance);

            // 2 máy x 500.000 x 3 ngày + cọc 2 x 2.000.000.
            CheckoutRequest request = new(
                [cartItemId],
                new("Hồ Chí Minh", "123 Nguyễn Huệ, Quận 1", "Nguyễn Văn A", "0901234567", null),
                new("VCB", "0123456789", "NGUYEN VAN A"),
                "v1.0",
                true,
                7_000_000m);

            CheckoutResult[] concurrent = await Task.WhenAll(
                Task.Run(() => SubmitAsync(options, locks, request)),
                Task.Run(() => SubmitAsync(options, locks, request)));
            CheckoutResult repeated = await SubmitAsync(options, locks, request);

            Assert.All(concurrent, r => Assert.True(r.IsSuccess, r.Error.Code));
            Assert.Single(concurrent, r => r.IsReplay);
            Assert.True(repeated.IsSuccess, repeated.Error.Code);
            Assert.True(repeated.IsReplay);

            await using RentalServiceDbContext context = new(options);
            RentalOrder order = await context.RentalOrders
                .Include(o => o.Items).ThenInclude(i => i.UnitAssignments)
                .SingleAsync();

            Assert.All(concurrent, r => Assert.Equal(order.Id, r.Order!.Id));
            Assert.Equal(order.Id, repeated.Order!.Id);
            Assert.Equal("DH-2026-000001", order.OrderCode);
            Assert.Equal(RentalOrderStatus.PendingPayment, order.Status);
            Assert.Equal(Now.AddMinutes(30), order.PaymentDeadline);
            Assert.Equal("INV-1", order.InvoiceRef);
            Assert.Equal(7_000_000m, order.TotalPayableAmount);
            Assert.Equal(2, Assert.Single(order.Items).UnitAssignments.Count);
            Assert.Equal(0, await context.RentalCartItems.CountAsync());

            Assert.Single(_inventory.HoldRequests);
            Assert.Empty(_inventory.Releases);
            Assert.Single(_payment.InvoiceRequests);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    private async Task<Guid> SeedAsync(DbContextOptions<RentalServiceDbContext> options)
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, Now).Value;
        Guid cartItemId = cart.AddItem(
            Guid.NewGuid(), _listingId, _supplierId, 2, new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 14), Now).Value.Id;

        await using RentalServiceDbContext context = new(options);
        await context.Database.MigrateAsync();
        context.RentalCarts.Add(cart);
        await context.SaveChangesAsync();

        _listings.Listings[_listingId] = new(
            _listingId,
            _supplierId,
            true,
            "Sony A7 IV",
            _equipmentTypeId,
            _brandId,
            "A7 IV",
            1,
            """{"sensor":"full-frame"}""",
            Guid.NewGuid(),
            500_000m,
            2_000_000m,
            "VND",
            "Quận 1, TP.HCM",
            "listing-v1");
        _listings.AvailableQuantities[_listingId] = 10;
        _catalog.EquipmentTypeNames[_equipmentTypeId] = "Máy ảnh";
        _catalog.BrandNames[_brandId] = "Sony";

        return cartItemId;
    }

    private async Task<CheckoutResult> SubmitAsync(
        DbContextOptions<RentalServiceDbContext> options,
        PostgresOrderLockManager locks,
        CheckoutRequest request)
    {
        await using RentalServiceDbContext context = new(options);
        RentalOrderRepository orders = new(context);
        RentalCartRepository carts = new(context);
        FixedTimeProvider clock = new(Now);
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
                clock),
            holds,
            new CheckoutOrderWriter(
                orders, carts, new PostgresOrderCodeGenerator(context), holds, context, NullLogger<CheckoutOrderWriter>.Instance),
            new CheckoutInvoiceIssuer(_payment, orders, context, clock, NullLogger<CheckoutInvoiceIssuer>.Instance),
            orders,
            locks,
            clock);

        return await service.SubmitAsync(new(_renterId, true), request, Key);
    }
}
