using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Services.Checkout;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
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
    private readonly FakeInventoryListingClient _listings = new();
<<<<<<< HEAD
    private readonly FakeCatalogReferenceClient _catalog = new();
=======
    private readonly FakeInventoryRentalClient _inventory = new();
    private readonly FakePaymentRentalClient _payment = new();
>>>>>>> origin/rental/fix/sub-order-and-order-queries

    [Fact]
    public async Task Submit_ConcurrentAndRepeatedSameKey_CreatesOnePendingOperation()
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
                3_000_000m);

            // Consumer và worker dùng chung cho mọi lần gửi, như một instance Rental đang chạy.
            FixedTimeProvider clock = new(Now);
            CheckoutCompletion completion = new(
                () => new RentalServiceDbContext(options), clock, _inventory, _payment, locks,
                context => new PostgresOrderCodeGenerator(context));

<<<<<<< HEAD
            var concurrent = await Task.WhenAll(
                Task.Run(() => SubmitAsync(options, locks, request)),
                Task.Run(() => SubmitAsync(options, locks, request)));
            var repeated = await SubmitAsync(options, locks, request);
=======
            CheckoutResult[] concurrent = await Task.WhenAll(
                Task.Run(() => SubmitAsync(options, locks, request, completion)),
                Task.Run(() => SubmitAsync(options, locks, request, completion)));
            CheckoutResult repeated = await SubmitAsync(options, locks, request, completion);
>>>>>>> origin/rental/fix/sub-order-and-order-queries

            Assert.All(concurrent, r => Assert.True(r.IsSuccess, r.Error.Code));
            Assert.True(repeated.IsSuccess, repeated.Error.Code);
            Assert.All(concurrent, r => Assert.Equal(repeated.Value.OperationId, r.Value.OperationId));

            await using RentalServiceDbContext context = new(options);
<<<<<<< HEAD
            var operation = await context.RentalCheckoutOperations.SingleAsync();
            Assert.Equal(repeated.Value.OperationId, operation.OperationId);
            Assert.Equal(CheckoutOperationStatus.Processing, operation.Status);
            Assert.Equal(Now.AddMinutes(30), operation.HoldExpiresAt);
            Assert.Single(await context.RentalOutboxMessages.Where(m => m.MessageType == "CreateHold").ToListAsync());
            Assert.Empty(await context.RentalOrders.ToListAsync());
            Assert.Single(await context.RentalCartItems.ToListAsync());
=======
            RentalOrder order = await context.RentalOrders
                .Include(o => o.Items).ThenInclude(i => i.UnitAssignments)
                .SingleAsync();

            Assert.All(concurrent, r => Assert.Equal(order.Id, r.Order!.Id));
            Assert.Equal(order.Id, repeated.Order!.Id);
            Assert.Equal("DH-2026-000001", order.OrderCode);
            Assert.Equal(RentalOrderStatus.PendingPayment, order.Status);
            Assert.Equal(Now.AddMinutes(30), order.PaymentDeadline);
            Assert.Equal("INV-1", order.InvoiceRef);
            Assert.Equal(3_000_000m, order.TotalPayableAmount);
            Assert.Equal(2, Assert.Single(order.Items).UnitAssignments.Count);
            Assert.Equal(0, await context.RentalCartItems.CountAsync());

            // Cùng Idempotency-Key chỉ ghi một lệnh giữ lịch và Inventory chỉ giữ máy một lần.
            Assert.Equal(1, await context.RentalOutboxMessages.CountAsync(m => m.MessageType == KafkaContractSpec.CommandTypes.CreateHold));
            Assert.Single(_inventory.HoldRequests);
            Assert.Empty(_inventory.Releases);
            Assert.Single(_payment.InvoiceRequests);
>>>>>>> origin/rental/fix/sub-order-and-order-queries
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

        _listings.Listings[_listingId] = TestListings.Create(_listingId, _supplierId, "Sony A7 IV", 500_000m, thumbnailMediaId: Guid.NewGuid());
        _listings.AvailableQuantities[_listingId] = 10;

        return cartItemId;
    }

    private async Task<Dprental.RentalService.Common.Result<CheckoutOperationAcceptedResponse>> SubmitAsync(
        DbContextOptions<RentalServiceDbContext> options,
        PostgresOrderLockManager locks,
        CheckoutRequest request,
        CheckoutCompletion completion)
    {
        CheckoutSubmitResult accepted;
        await using (RentalServiceDbContext context = new(options))
        {
            accepted = await NewService(context, locks).SubmitAsync(new(_renterId, true), request, Key);
        }

        return accepted.IsFailure
            ? accepted.Failure!
            : await completion.CompleteAsync(_renterId, Key);
    }

    private CheckoutService NewService(RentalServiceDbContext context, PostgresOrderLockManager locks)
    {
        RentalOrderRepository orders = new(context);
        RentalCartRepository carts = new(context);
        OutboxRepository outbox = new(context);
        FixedTimeProvider clock = new(Now);
        InventoryCommandProducer inventoryCommands = new(outbox, Microsoft.Extensions.Options.Options.Create(new KafkaOptions { Enabled = true }), clock);
<<<<<<< HEAD

=======
>>>>>>> origin/rental/fix/sub-order-and-order-queries

        return new CheckoutService(
            new CheckoutPreparationService(
                carts,
                orders,
                _listings,
                Microsoft.Extensions.Options.Options.Create(new RentalTermsOptions
                {
                    CurrentVersion = "v1.0",
                    DocumentRef = "terms/v1.0.pdf",
                    ContentHash = "hash123456"
                }),
                clock),
            new RentalCheckoutOperationRepository(context),
            orders,
            locks,
            inventoryCommands,
            context,
            clock);
<<<<<<< HEAD

        return await service.SubmitAsync(new(_renterId, true), request, Key);
=======
>>>>>>> origin/rental/fix/sub-order-and-order-queries
    }
}
