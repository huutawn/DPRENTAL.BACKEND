using Dprental.RentalService.Application.IServices.Reminders;
using Dprental.RentalService.Application.Services.Reminders;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Api.Tests.Orders;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Background;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Background;

public sealed class RentalReminderWorkerTests
{
    private static readonly TimeSpan VnOffset = TimeSpan.FromHours(7);
    private readonly string _dbName = $"ReminderWorker_{Guid.NewGuid():N}";
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _supplierId = Guid.NewGuid();

    private RentalServiceDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<RentalServiceDbContext>().UseInMemoryDatabase(_dbName).Options);

    [Fact]
    public async Task ProcessBatchAsync_WhenNoDueSchedules_ReturnsZero()
    {
        await using var db = CreateDbContext();
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddScoped<IRentalReminderScheduleRepository, RentalReminderScheduleRepository>();
        services.AddScoped<IRentalOrderRepository, RentalOrderRepository>();
        services.AddSingleton<IOrderLockManager, FakeOrderLockManager>();
        services.AddSingleton<IRentalEventProducer, FakeRentalEventProducer>();
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<RentalServiceDbContext>());
        var sp = services.BuildServiceProvider();

        var scopeFactoryMock = new Moq.Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(() =>
        {
            var scopeMock = new Moq.Mock<IServiceScope>();
            scopeMock.Setup(s => s.ServiceProvider).Returns(sp);
            return scopeMock.Object;
        });

        var worker = new RentalReminderWorker(
            scopeFactoryMock.Object,
            Options.Create(new RentalReminderOptions { BatchSize = 10 }),
            NullLogger<RentalReminderWorker>.Instance);

        var result = await worker.ProcessBatchAsync(CancellationToken.None);
        Assert.Equal(0, result.Claimed);
        Assert.Equal(0, result.Processed);
        Assert.Equal(0, result.NoticesSent);
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenHandoverReminderDue_EnqueuesEventAndAdvancesSchedule()
    {
        // Target start date is 2026-10-12. Current time is D-2: 2026-10-10 at 09:00 VN
        var now = new DateTimeOffset(2026, 10, 10, 9, 0, 0, VnOffset);
        var timeProvider = new Dprental.RentalService.Api.Tests.Checkout.FixedTimeProvider(now);

        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        var fakeProducer = new FakeRentalEventProducer();

        await using (var db = CreateDbContext())
        {
            var order = OrderTestData.Paid(
                OrderTestData.Order(_renterId, "DH-2026-REM-01", now.AddDays(-5),
                    OrderTestData.Line(_supplierId, "Máy cẩu", "Cẩu", "Kato", 1, 1000m, 5000m)),
                now.AddDays(-5));
            typeof(RentalOrder).GetProperty(nameof(RentalOrder.Id))!.SetValue(order, orderId);
            typeof(RentalItem).GetProperty(nameof(RentalItem.Id))!.SetValue(order.Items.First(), itemId);
            typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(order.Items.First(), RentalItemStatus.AwaitingHandover);

            db.RentalOrders.Add(order);

            // Due schedule
            var schedule = RentalReminderSchedule.Create(
                orderId, itemId, RentalReminderScheduleType.Handover, 1, now.AddMinutes(-10), now.AddDays(-2));
            db.RentalReminderSchedules.Add(schedule);
            await db.SaveChangesAsync();
        }

        await using (var db = CreateDbContext())
        {
            var services = new ServiceCollection();
            services.AddSingleton(db);
            services.AddScoped<IRentalReminderScheduleRepository, RentalReminderScheduleRepository>();
            services.AddScoped<IRentalOrderRepository, RentalOrderRepository>();
            services.AddSingleton<IOrderLockManager, FakeOrderLockManager>();
            services.AddSingleton<IRentalEventProducer>(fakeProducer);
            services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<RentalServiceDbContext>());
            services.AddSingleton<TimeProvider>(timeProvider);
            services.AddScoped<IRentalReminderBatchProcessor, RentalReminderBatchProcessor>();
            var sp = services.BuildServiceProvider();

            var scopeFactoryMock = new Moq.Mock<IServiceScopeFactory>();
            scopeFactoryMock.Setup(s => s.CreateScope()).Returns(() =>
            {
                var scopeMock = new Moq.Mock<IServiceScope>();
                scopeMock.Setup(s => s.ServiceProvider).Returns(sp);
                return scopeMock.Object;
            });

            var worker = new RentalReminderWorker(
                scopeFactoryMock.Object,
                Options.Create(new RentalReminderOptions { BatchSize = 10 }),
                NullLogger<RentalReminderWorker>.Instance);

            var result = await worker.ProcessBatchAsync(CancellationToken.None);

            Assert.Equal(1, result.Claimed);
            Assert.Equal(1, result.Processed);
            Assert.Equal(1, result.NoticesSent);
            Assert.Equal(0, result.Failed);

            // Verify event in producer
            var ev = Assert.Single(fakeProducer.ReminderDueEvents);
            Assert.Equal(orderId, ev.RentalOrderId);
            Assert.Equal(itemId, ev.RentalItemId);
            Assert.Equal("HandoverReminder", ev.ReminderType);
            Assert.Equal(new DateOnly(2026, 10, 10), ev.ReminderDate);
            Assert.Equal(1, ev.RemainingUnitCount);

            // Verify schedule was advanced
            var updatedSchedule = await db.RentalReminderSchedules.FirstAsync();
            Assert.Equal(new DateOnly(2026, 10, 10), updatedSchedule.LastNotifiedDate);
            Assert.Null(updatedSchedule.LeasedBy); // lease released
            Assert.NotNull(updatedSchedule.NextTriggerAt);
            // Next trigger is D-1 at 08:00 VN
            var expectedTomorrow = new DateTimeOffset(2026, 10, 11, 8, 0, 0, VnOffset);
            Assert.Equal(expectedTomorrow, updatedSchedule.NextTriggerAt);
        }
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenHandoverInProgress_SkipsItemAndReleasesLease()
    {
        var now = new DateTimeOffset(2026, 10, 10, 9, 0, 0, VnOffset);
        var timeProvider = new Dprental.RentalService.Api.Tests.Checkout.FixedTimeProvider(now);

        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        var fakeProducer = new FakeRentalEventProducer();

        await using (var db = CreateDbContext())
        {
            var order = OrderTestData.Paid(
                OrderTestData.Order(_renterId, "DH-2026-REM-02", now.AddDays(-5),
                    OrderTestData.Line(_supplierId, "Máy cẩu", "Cẩu", "Kato", 1, 1000m, 5000m)),
                now.AddDays(-5));
            typeof(RentalOrder).GetProperty(nameof(RentalOrder.Id))!.SetValue(order, orderId);
            typeof(RentalItem).GetProperty(nameof(RentalItem.Id))!.SetValue(order.Items.First(), itemId);
            typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(order.Items.First(), RentalItemStatus.AwaitingHandover);

            // Add pending handover receipt
            order.AddOperationReceipt(new RentalOperationReceipt(
                Guid.NewGuid(), orderId, "op-handover-pending", "MarkAssetsHandedOver", "fp", false, null, null, now,
                RentalOperationReceipt.StatusPending, targetItemId: itemId));

            db.RentalOrders.Add(order);

            var schedule = RentalReminderSchedule.Create(
                orderId, itemId, RentalReminderScheduleType.Handover, 1, now.AddMinutes(-5), now.AddDays(-2));
            db.RentalReminderSchedules.Add(schedule);
            await db.SaveChangesAsync();
        }

        await using (var db = CreateDbContext())
        {
            var services = new ServiceCollection();
            services.AddSingleton(db);
            services.AddScoped<IRentalReminderScheduleRepository, RentalReminderScheduleRepository>();
            services.AddScoped<IRentalOrderRepository, RentalOrderRepository>();
            services.AddSingleton<IOrderLockManager, FakeOrderLockManager>();
            services.AddSingleton<IRentalEventProducer>(fakeProducer);
            services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<RentalServiceDbContext>());
            services.AddSingleton<TimeProvider>(timeProvider);
            services.AddScoped<IRentalReminderBatchProcessor, RentalReminderBatchProcessor>();
            var sp = services.BuildServiceProvider();

            var scopeFactoryMock = new Moq.Mock<IServiceScopeFactory>();
            scopeFactoryMock.Setup(s => s.CreateScope()).Returns(() =>
            {
                var scopeMock = new Moq.Mock<IServiceScope>();
                scopeMock.Setup(s => s.ServiceProvider).Returns(sp);
                return scopeMock.Object;
            });

            var worker = new RentalReminderWorker(
                scopeFactoryMock.Object,
                Options.Create(new RentalReminderOptions { BatchSize = 10 }),
                NullLogger<RentalReminderWorker>.Instance);

            var result = await worker.ProcessBatchAsync(CancellationToken.None);

            Assert.Equal(1, result.Claimed);
            Assert.Equal(1, result.Processed);
            Assert.Equal(0, result.NoticesSent); // Skipped because handover is in progress!
            Assert.Empty(fakeProducer.ReminderDueEvents);

            // Lease was released so it can be re-evaluated later
            var updatedSchedule = await db.RentalReminderSchedules.FirstAsync();
            Assert.Null(updatedSchedule.LeasedBy);
            Assert.Null(updatedSchedule.LastNotifiedDate);
        }
    }
}
