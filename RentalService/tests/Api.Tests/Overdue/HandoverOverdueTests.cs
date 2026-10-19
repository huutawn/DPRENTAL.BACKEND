using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Api.Tests.Orders;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Background;
using Dprental.RentalService.Application.DTOs.Cancellation;
using Dprental.RentalService.Application.IServices.Cancellation;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.IServices.Overdue;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Services.Cancellation;
using Dprental.RentalService.Application.Services.Overdue;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Overdue;

public sealed class HandoverOverdueTests
{
    private readonly string _dbName = $"HandoverOverdue_{Guid.NewGuid():N}";
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _supplierId = Guid.NewGuid();
    private readonly AdjustableTimeProvider _clock = new(new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.FromHours(7)));
    private readonly Mock<IRentalOperationExecutor> _mockExecutor = new();
    private RentalOrder _order = null!;
    private RentalItem _item = null!;

    private RentalServiceDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalServiceDbContext>().UseInMemoryDatabase(_dbName).Options);

    private ServiceProvider CreateServiceProvider(RentalServiceDbContext context)
    {
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddScoped<IRentalOrderRepository, RentalOrderRepository>();
        services.AddSingleton<IOrderLockManager, FakeOrderLockManager>();
        services.AddSingleton(_mockExecutor.Object);
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<RentalServiceDbContext>());
        services.AddSingleton<TimeProvider>(_clock);
        services.AddScoped<IItemCancellationService, ItemCancellationService>();
        services.AddScoped<IHandoverOverdueService, HandoverOverdueService>();
        services.AddLogging();
        return services.BuildServiceProvider();
    }

    private async Task SeedAsync(RentalItemStatus itemStatus = RentalItemStatus.AwaitingHandover)
    {
        _order = OrderTestData.Paid(
            OrderTestData.Order(
                _renterId,
                "DH-2026-OVERDUE-01",
                OrderTestData.Now,
                OrderTestData.Line(_supplierId, "Máy lu rung Sakai SV520", "Máy lu rung", "Sakai", 1, 1_500_000m, 10_000_000m)),
            OrderTestData.Now.AddMinutes(5));

        _item = _order.Items.Single();
        typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(_item, itemStatus);
        typeof(RentalOrder).GetProperty(nameof(RentalOrder.Status))!.SetValue(_order, RentalOrderStatus.AwaitingHandover);

        OrderTestData.LinkItemsToOrder(_order);

        await using RentalServiceDbContext context = NewContext();
        context.RentalOrders.Add(_order);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task ProcessOrderAsync_WhenPastOverdueMilestone_EmitsNoticeOnce_AndSecondRunDoesNotEmitAgain()
    {
        await SeedAsync(RentalItemStatus.AwaitingHandover);

        // Start is 2026-10-12, End is 2026-10-15.
        // Clock is 2026-10-13 08:00 (past Start + 1, but before End + 1)
        await using var context = NewContext();
        var sp = CreateServiceProvider(context);
        var service = sp.GetRequiredService<IHandoverOverdueService>();

        // First run: should send notice
        var firstResult = await service.ProcessOrderAsync(_order.Id);
        Assert.Equal(1, firstResult.ProcessedItems);
        Assert.Equal(1, firstResult.NoticesSent);
        Assert.Equal(0, firstResult.AutoCancelled);

        // Verify receipt and outbox
        var receipt = await context.RentalOperationReceipts.SingleAsync(r => r.Action == "HandoverOverdueNotice");
        Assert.Equal(_item.Id, receipt.TargetItemId);
        Assert.Equal(RentalOperationReceipt.StatusSucceeded, receipt.Status);

        var outbox = await context.RentalOutboxMessages.SingleAsync(m => m.MessageType == "RentalHandoverOverdue");
        Assert.Equal(_order.Id.ToString("D"), outbox.PartitionKey);

        // Second run: should NOT send again
        var secondResult = await service.ProcessOrderAsync(_order.Id);
        Assert.Equal(0, secondResult.ProcessedItems);
        Assert.Equal(0, secondResult.NoticesSent);
        Assert.Single(context.RentalOperationReceipts.Where(r => r.Action == "HandoverOverdueNotice"));
    }

    [Fact]
    public async Task ProcessOrderAsync_WhenPastAutoCancelMilestone_AutoCancelsItem_SchedulesReleaseAndRefund()
    {
        await SeedAsync(RentalItemStatus.AwaitingHandover);

        // ReturnDate is 2026-10-15. Auto-cancellation milestone is 2026-10-16 00:00 VN.
        // Advance clock past auto-cancellation
        _clock.UtcNow = new DateTimeOffset(2026, 10, 16, 08, 0, 0, TimeSpan.FromHours(7));

        await using var context = NewContext();
        var sp = CreateServiceProvider(context);
        var service = sp.GetRequiredService<IHandoverOverdueService>();

        var result = await service.ProcessOrderAsync(_order.Id);
        Assert.Equal(1, result.ProcessedItems);
        Assert.Equal(1, result.AutoCancelled);
        Assert.Equal(0, result.NoticesSent);

        // Verify item is cancelled
        var dbOrder = await context.RentalOrders.Include(o => o.Items).SingleAsync(o => o.Id == _order.Id);
        Assert.Equal(RentalOrderStatus.Cancelled, dbOrder.Status);
        Assert.Equal(RentalItemStatus.Cancelled, dbOrder.Items.Single().Status);

        // Verify executor was called for release and refund
        _mockExecutor.Verify(x => x.ScheduleReleaseAndRefundAsync(
            It.Is<RentalOrder>(o => o.Id == _order.Id),
            It.Is<RentalItem>(i => i.Id == _item.Id),
            Guid.Empty,
            "Hết kỳ thuê mà chưa bàn giao thiết bị",
            _clock.GetUtcNow(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessOrderAsync_WhenHandoverInProgress_SkipsItem()
    {
        await SeedAsync(RentalItemStatus.AwaitingHandover);

        // Past auto-cancel milestone
        _clock.UtcNow = new DateTimeOffset(2026, 10, 16, 08, 0, 0, TimeSpan.FromHours(7));

        await using var context = NewContext();
        // Add pending handover receipt
        var pendingHandoverReceipt = new RentalOperationReceipt(
            Guid.NewGuid(),
            _order.Id,
            "handover-in-flight",
            "MarkAssetsHandedOver",
            "fp-handover",
            isSuccess: false,
            errorCode: null,
            errorMessage: null,
            createdAt: _clock.GetUtcNow(),
            status: RentalOperationReceipt.StatusPending,
            targetItemId: _item.Id);
        context.RentalOperationReceipts.Add(pendingHandoverReceipt);
        await context.SaveChangesAsync();

        var sp = CreateServiceProvider(context);
        var service = sp.GetRequiredService<IHandoverOverdueService>();

        var result = await service.ProcessOrderAsync(_order.Id);
        Assert.Equal(0, result.ProcessedItems);
        Assert.Equal(0, result.AutoCancelled);
        Assert.Equal(0, result.NoticesSent);

        // Status remains AwaitingHandover
        var dbOrder = await context.RentalOrders.Include(o => o.Items).SingleAsync(o => o.Id == _order.Id);
        Assert.Equal(RentalItemStatus.AwaitingHandover, dbOrder.Items.Single().Status);
    }

    [Fact]
    public async Task Worker_ProcessBatchAsync_ScansAndProcessesOrders()
    {
        await SeedAsync(RentalItemStatus.AwaitingHandover);

        await using var context = NewContext();
        var sp = CreateServiceProvider(context);

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(() =>
        {
            var scopeMock = new Mock<IServiceScope>();
            scopeMock.Setup(s => s.ServiceProvider).Returns(sp);
            return scopeMock.Object;
        });

        var options = Microsoft.Extensions.Options.Options.Create(new HandoverOverdueOptions
        {
            Enabled = true,
            ScanIntervalSeconds = 60,
            BatchSize = 10
        });

        var worker = new HandoverOverdueWorker(
            scopeFactoryMock.Object,
            options,
            NullLogger<HandoverOverdueWorker>.Instance);

        var batchResult = await worker.ProcessBatchAsync(CancellationToken.None);
        Assert.Equal(1, batchResult.Orders);
        Assert.Equal(1, batchResult.ProcessedItems);
        Assert.Equal(1, batchResult.NoticesSent);
        Assert.Equal(0, batchResult.Failed);
    }

    [Fact]
    public async Task Concurrency_WorkerAndRenterConcurrentCancellation_OnlyOneCancelsAndOneRefundScheduled()
    {
        await SeedAsync(RentalItemStatus.AwaitingHandover);

        // Advance to auto-cancel time
        _clock.UtcNow = new DateTimeOffset(2026, 10, 16, 08, 0, 0, TimeSpan.FromHours(7));

        await using var context = NewContext();
        var sp = CreateServiceProvider(context);
        var overdueService = sp.GetRequiredService<IHandoverOverdueService>();
        var cancelService = sp.GetRequiredService<IItemCancellationService>();

        // Run both worker auto-cancel and renter cancel
        var workerTask = overdueService.ProcessOrderAsync(_order.Id);
        var renterTask = cancelService.CancelByRenterAsync(
            _item.Id,
            new CancelNotDeliveredRequest("Renter hủy đồng thời"),
            "idemp-race-key",
            OrderTestData.User(_renterId));

        await Task.WhenAll(workerTask, renterTask);

        var renterResult = await renterTask;
        Assert.True(renterResult.IsSuccess);
        Assert.Equal(RentalItemStatus.Cancelled.ToString(), renterResult.Value.Status);

        // Verify executor was called AT MOST ONCE for release and refund
        _mockExecutor.Verify(x => x.ScheduleReleaseAndRefundAsync(
            It.IsAny<RentalOrder>(),
            It.IsAny<RentalItem>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
