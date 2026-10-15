using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.Checkout;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Checkout;

public sealed class CheckoutInvoiceIssuerTests
{
    // 10:00 ngày 09/10/2026 giờ Việt Nam; đơn chờ thanh toán đến 10:30.
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 3, 0, 0, TimeSpan.Zero);
    private const string OperationId = "checkout-op-1";

    private readonly string _dbName = $"CheckoutInvoice_{Guid.NewGuid():N}";
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly FakePaymentRentalClient _payment = new();
    private readonly AdjustableTimeProvider _clock = new(Now.AddSeconds(1));
    private readonly Guid _orderId;

    public CheckoutInvoiceIssuerTests()
    {
        RentalOrder order = CheckoutTestOrders.CreatePending(_renterId, OperationId, Now);
        _orderId = order.Id;

        using RentalServiceDbContext context = NewContext();
        context.RentalOrders.Add(order);
        context.SaveChanges();
    }

    private RentalServiceDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalServiceDbContext>().UseInMemoryDatabase(_dbName).Options);

    private CheckoutInvoiceIssuer CreateIssuer(RentalServiceDbContext context, IUnitOfWork? unitOfWork = null) => new(
        _payment,
        new RentalOrderRepository(context),
        unitOfWork ?? context,
        _clock,
        NullLogger<CheckoutInvoiceIssuer>.Instance);

    private static async Task<RentalOrder> LoadAsync(RentalServiceDbContext context) =>
        (await new RentalOrderRepository(context).GetByCheckoutOperationIdAsync(OperationId))!;

    private async Task<Result> EnsureInvoiceAsync()
    {
        await using RentalServiceDbContext context = NewContext();
        return await CreateIssuer(context).EnsureInvoiceAsync(await LoadAsync(context));
    }

    private async Task<RentalOrder> ReloadAsync()
    {
        await using RentalServiceDbContext context = NewContext();
        return await LoadAsync(context);
    }

    [Fact]
    public async Task EnsureInvoice_SendsRentalPlusDepositOfEveryItem_AndStoresInvoiceRef()
    {
        Result result = await EnsureInvoiceAsync();

        Assert.True(result.IsSuccess);
        RentalOrder order = await ReloadAsync();
        Assert.Equal("INV-1", order.InvoiceRef);
        Assert.Equal(RentalOrderStatus.PendingPayment, order.Status);

        CreateInvoiceRequest request = Assert.Single(_payment.InvoiceRequests);
        Assert.Equal(_orderId, request.RentalOrderId);
        Assert.Equal("DH-2026-000042", request.OrderCode);
        Assert.Equal(_renterId, request.RenterId);
        Assert.Equal("VND", request.Currency);
        Assert.Equal(Now.AddMinutes(30), request.PaymentDeadline);
        Assert.Equal(RentalOperationExecutor.ComputeOperationKey("CreateInvoice", _orderId.ToString()), request.OperationKey);
        Assert.Equal(CheckoutTestOrders.TotalRentalAmount + CheckoutTestOrders.TotalDepositAmount, request.TotalAmount);

        Assert.Equal(
            order.Items.Select(i => (i.Id, i.SupplierId, i.RentalAmount, i.DepositAmount)).Order(),
            request.Items.Select(i => (i.RentalItemId, i.SupplierId, i.RentalAmount, i.DepositAmount)).Order());
        Assert.Equal(CheckoutTestOrders.TotalRentalAmount, request.Items.Sum(i => i.RentalAmount));
        Assert.Equal(CheckoutTestOrders.TotalDepositAmount, request.Items.Sum(i => i.DepositAmount));
    }

    [Fact]
    public async Task EnsureInvoice_DoesNotCallPayment_WhenOrderAlreadyHasInvoice()
    {
        await EnsureInvoiceAsync();

        Result result = await EnsureInvoiceAsync();

        Assert.True(result.IsSuccess);
        Assert.Single(_payment.InvoiceRequests);
        Assert.Equal("INV-1", (await ReloadAsync()).InvoiceRef);
    }

    [Fact]
    public async Task EnsureInvoice_RetriesOnceWithTheSameOperationKey_WhenPaymentFailsTemporarily()
    {
        _payment.Failures.Enqueue(FakePaymentRentalClient.Down);

        Result result = await EnsureInvoiceAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(2, _payment.InvoiceRequests.Count);
        Assert.Single(_payment.InvoiceRequests.Select(r => r.OperationKey).Distinct());
        Assert.Equal(1, _payment.CreatedInvoiceCount);
        Assert.Equal("INV-1", (await ReloadAsync()).InvoiceRef);
    }

    [Theory]
    [InlineData(ErrorType.ServiceUnavailable)]
    [InlineData(ErrorType.Failure)]
    public async Task EnsureInvoice_KeepsOrderWithoutInvoice_WhenPaymentStaysUnavailable(ErrorType errorType)
    {
        _payment.AlwaysFailWith = new Error("Payment.Http503", "PaymentService trả về lỗi 503.", errorType);

        Result result = await EnsureInvoiceAsync();

        Assert.Equal(CheckoutErrors.InvoiceTemporarilyUnavailable, result.Error);
        Assert.Equal(2, _payment.InvoiceRequests.Count);
        RentalOrder order = await ReloadAsync();
        Assert.Null(order.InvoiceRef);
        Assert.Equal(RentalOrderStatus.PendingPayment, order.Status);
    }

    [Fact]
    public async Task EnsureInvoice_DoesNotRetry_WhenPaymentRejectsTheInvoice()
    {
        _payment.AlwaysFailWith = Error.Conflict(PaymentContractSpec.ErrorCodes.TotalAmountMismatch, "Tổng tiền không khớp.");

        Result result = await EnsureInvoiceAsync();

        Assert.Equal(CheckoutErrors.InvoiceRejected, result.Error);
        Assert.Single(_payment.InvoiceRequests);
        Assert.Null((await ReloadAsync()).InvoiceRef);
    }

    [Fact]
    public async Task EnsureInvoice_DoesNotStoreInvoice_WhenPaymentReturnsADifferentTotal()
    {
        _payment.Tamper = invoice => invoice with { TotalAmount = invoice.TotalAmount - 1 };

        Result result = await EnsureInvoiceAsync();

        Assert.Equal(CheckoutErrors.InvoiceRejected, result.Error);
        Assert.Null((await ReloadAsync()).InvoiceRef);
    }

    [Fact]
    public async Task EnsureInvoice_DoesNotStoreInvoice_WhenPaymentReturnsADifferentCurrency()
    {
        _payment.Tamper = invoice => invoice with { Currency = "USD" };

        Result result = await EnsureInvoiceAsync();

        Assert.Equal(CheckoutErrors.InvoiceRejected, result.Error);
        Assert.Null((await ReloadAsync()).InvoiceRef);
    }

    [Fact]
    public async Task EnsureInvoice_DoesNotStoreInvoice_WhenPaymentReturnsBlankInvoiceRef()
    {
        _payment.Tamper = invoice => invoice with { InvoiceRef = " " };

        Result result = await EnsureInvoiceAsync();

        Assert.Equal(CheckoutErrors.InvoiceRejected, result.Error);
        Assert.Null((await ReloadAsync()).InvoiceRef);
    }

    [Fact]
    public async Task EnsureInvoice_ReturnsOrderExpired_WithoutCallingPayment_WhenPaymentDeadlineHasPassed()
    {
        _clock.UtcNow = Now.AddMinutes(30);

        Result result = await EnsureInvoiceAsync();

        Assert.Equal(CheckoutErrors.OrderExpired, result.Error);
        Assert.Empty(_payment.InvoiceRequests);
    }

    [Fact]
    public async Task EnsureInvoice_ReturnsOrderExpired_WhenOrderIsNoLongerPendingPayment()
    {
        await using (RentalServiceDbContext context = NewContext())
        {
            RentalOrder order = await LoadAsync(context);
            order.MarkPaymentExpired(Now.AddMinutes(30), "expiry-op");
            await context.SaveChangesAsync();
        }

        Result result = await EnsureInvoiceAsync();

        Assert.Equal(CheckoutErrors.OrderExpired, result.Error);
        Assert.Empty(_payment.InvoiceRequests);
    }

    [Fact]
    public async Task EnsureInvoice_DiscardsChanges_WhenOrderWasChangedByAnotherWriter()
    {
        await using RentalServiceDbContext context = NewContext();
        RentalOrder order = await LoadAsync(context);

        Result result = await CreateIssuer(context, new ConflictingUnitOfWork(context)).EnsureInvoiceAsync(order);

        Assert.Equal(CheckoutErrors.InvoiceTemporarilyUnavailable, result.Error);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Null((await ReloadAsync()).InvoiceRef);
    }

    /// <summary>Giả lập đơn vừa bị luồng khác (ví dụ quét hết hạn) sửa trước khi lưu InvoiceRef.</summary>
    private sealed class ConflictingUnitOfWork(RentalServiceDbContext inner) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new DbUpdateConcurrencyException("Simulated concurrent update.");

        public void DiscardChanges() => inner.DiscardChanges();
        public Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            inner.BeginTransactionAsync(cancellationToken);
    }
}
