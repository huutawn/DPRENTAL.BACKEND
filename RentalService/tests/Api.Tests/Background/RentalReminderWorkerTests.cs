using Dprental.RentalService.Application.IServices.Reminders;
using Dprental.RentalService.Application.Services.Reminders;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Api.Tests.Orders;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Background;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Common;
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
            Assert.Equal(now.AddMinutes(5), updatedSchedule.NextTriggerAt);
        }
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenReturnAwaitingConfirmation_PausesReminderWithoutShiftingTrigger()
    {
        var now = new DateTimeOffset(2026, 10, 15, 9, 0, 0, VnOffset);
        var timeProvider = new Dprental.RentalService.Api.Tests.Checkout.FixedTimeProvider(now);

        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        DateTimeOffset originalTrigger = now.AddMinutes(-5);
        var fakeProducer = new FakeRentalEventProducer();

        await using (var db = CreateDbContext())
        {
            var order = OrderTestData.Paid(
                OrderTestData.Order(_renterId, "DH-2026-REM-03", now.AddDays(-10),
                    OrderTestData.Line(_supplierId, "Máy lu", "Lu", "Hamm", 1, 1500m, 6000m)),
                now.AddDays(-10));
            typeof(RentalOrder).GetProperty(nameof(RentalOrder.Id))!.SetValue(order, orderId);
            var item = order.Items.First();
            typeof(RentalItem).GetProperty(nameof(RentalItem.Id))!.SetValue(item, itemId);
            typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(item, RentalItemStatus.InRental);

            var assignmentId = item.UnitAssignments.First().Id;
            var reqUnit = new ReturnRequestUnit(Guid.NewGuid(), Guid.NewGuid(), assignmentId, Array.Empty<Guid>());
            var returnReq = new ReturnRequest(
                Guid.NewGuid(), itemId, now.AddHours(-1), "Traning hoan tat", _renterId, now.AddHours(-1), new[] { reqUnit });
            item.AddReturnRequest(returnReq);

            db.RentalOrders.Add(order);

            var schedule = RentalReminderSchedule.Create(
                orderId, itemId, RentalReminderScheduleType.Return, 1, originalTrigger, now.AddDays(-1));
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
            Assert.Equal(0, result.NoticesSent);
            Assert.Empty(fakeProducer.ReminderDueEvents);

            var updatedSchedule = await db.RentalReminderSchedules.FirstAsync();
            Assert.Null(updatedSchedule.LeasedBy);
            Assert.Null(updatedSchedule.LastNotifiedDate);
            Assert.Equal(originalTrigger, updatedSchedule.NextTriggerAt);
        }
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenFencingCheckFails_RollsBackTransactionImmediately()
    {
        var now = new DateTimeOffset(2026, 10, 10, 9, 0, 0, VnOffset);
        var timeProvider = new Dprental.RentalService.Api.Tests.Checkout.FixedTimeProvider(now);

        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();

        await using (var db = CreateDbContext())
        {
            var order = OrderTestData.Paid(
                OrderTestData.Order(_renterId, "DH-2026-REM-04", now.AddDays(-5),
                    OrderTestData.Line(_supplierId, "Máy đào", "Đào", "Komatsu", 1, 2000m, 8000m)),
                now.AddDays(-5));
            typeof(RentalOrder).GetProperty(nameof(RentalOrder.Id))!.SetValue(order, orderId);
            typeof(RentalItem).GetProperty(nameof(RentalItem.Id))!.SetValue(order.Items.First(), itemId);
            typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(order.Items.First(), RentalItemStatus.AwaitingHandover);

            db.RentalOrders.Add(order);

            var schedule = RentalReminderSchedule.Create(
                orderId, itemId, RentalReminderScheduleType.Handover, 1, now.AddMinutes(-5), now.AddDays(-2));
            db.RentalReminderSchedules.Add(schedule);
            await db.SaveChangesAsync();
        }

        await using (var db = CreateDbContext())
        {
            var realRepo = new RentalReminderScheduleRepository(db);
            var mockRepo = new Moq.Mock<IRentalReminderScheduleRepository>();
            mockRepo.Setup(r => r.ClaimDueBatchAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<TimeSpan>(), Moq.It.IsAny<int>(), Moq.It.IsAny<CancellationToken>()))
                .Returns((string w, TimeSpan l, int b, CancellationToken ct) => realRepo.ClaimDueBatchAsync(w, l, b, ct));
            mockRepo.Setup(r => r.GetOrderReminderProjectionAsync(orderId, Moq.It.IsAny<CancellationToken>()))
                .Returns((Guid id, CancellationToken ct) => realRepo.GetOrderReminderProjectionAsync(id, ct));
            mockRepo.Setup(r => r.LockSchedulesForUpdateAsync(Moq.It.IsAny<IEnumerable<Guid>>(), Moq.It.IsAny<CancellationToken>()))
                .Returns((IEnumerable<Guid> ids, CancellationToken ct) => realRepo.LockSchedulesForUpdateAsync(ids, ct));
            mockRepo.Setup(r => r.AdvanceScheduleWithLeaseCheckAsync(
                Moq.It.IsAny<Guid>(), Moq.It.IsAny<Guid>(), Moq.It.IsAny<DateOnly>(), Moq.It.IsAny<DateTimeOffset>(),
                Moq.It.IsAny<DateTimeOffset?>(), Moq.It.IsAny<bool>(), Moq.It.IsAny<bool>(), Moq.It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            var fakeProducer = new FakeRentalEventProducer();

            var services = new ServiceCollection();
            services.AddSingleton(db);
            services.AddSingleton<IRentalReminderScheduleRepository>(mockRepo.Object);
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
            Assert.Equal(0, result.NoticesSent);
            Assert.Equal(1, result.Failed);
        }
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenOrderInTerminalState_CompletesSchedule()
    {
        var now = new DateTimeOffset(2026, 10, 10, 9, 0, 0, VnOffset);
        var timeProvider = new Dprental.RentalService.Api.Tests.Checkout.FixedTimeProvider(now);

        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        var fakeProducer = new FakeRentalEventProducer();

        await using (var db = CreateDbContext())
        {
            var order = OrderTestData.Paid(
                OrderTestData.Order(_renterId, "DH-2026-REM-05", now.AddDays(-10),
                    OrderTestData.Line(_supplierId, "Máy khoan", "Khoan", "Bosch", 1, 500m, 2000m)),
                now.AddDays(-10));
            typeof(RentalOrder).GetProperty(nameof(RentalOrder.Id))!.SetValue(order, orderId);
            typeof(RentalOrder).GetProperty(nameof(RentalOrder.Status))!.SetValue(order, RentalOrderStatus.Cancelled);

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
            Assert.Equal(1, result.Completed);
            Assert.Equal(0, result.NoticesSent);
            Assert.Empty(fakeProducer.ReminderDueEvents);

            var updatedSchedule = await db.RentalReminderSchedules.FirstAsync();
            Assert.Equal(RentalReminderScheduleStatus.Completed, updatedSchedule.Status);
            Assert.Null(updatedSchedule.LeasedBy);
            Assert.Null(updatedSchedule.NextTriggerAt);
        }
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var scopeFactoryMock = new Moq.Mock<IServiceScopeFactory>();
        var worker = new RentalReminderWorker(
            scopeFactoryMock.Object,
            Options.Create(new RentalReminderOptions { BatchSize = 10 }),
            NullLogger<RentalReminderWorker>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ProcessBatchAsync(cts.Token));
    }

    [Fact]
    public async Task RentalEventProducer_EnqueueRentalReminderDue_UsesDeterministicLogicalEntityIdIgnoringCorrelationId()
    {
        var now = new DateTimeOffset(2026, 10, 10, 9, 0, 0, VnOffset);
        var timeProvider = new Dprental.RentalService.Api.Tests.Checkout.FixedTimeProvider(now);

        var payload = new RentalReminderDuePayload(
            RentalOrderId: Guid.NewGuid(),
            RentalItemId: Guid.NewGuid(),
            OrderCode: "DH-2026-REM-TEST",
            RenterId: Guid.NewGuid(),
            SupplierId: Guid.NewGuid(),
            ReminderType: "HandoverReminder",
            ReminderDate: new DateOnly(2026, 10, 10),
            ActionDeadline: new DateTimeOffset(2026, 10, 12, 8, 0, 0, VnOffset),
            RemainingUnitCount: 2,
            TargetDate: new DateOnly(2026, 10, 12),
            OrderVersion: 1);

        Guid capturedMessageId1 = Guid.Empty;
        Guid capturedMessageId2 = Guid.Empty;

        var mockWriter = new Moq.Mock<IKafkaOutboxWriter>();
        mockWriter.Setup(w => w.EnqueueAsync(
            Moq.It.IsAny<string>(),
            Moq.It.IsAny<string>(),
            Moq.It.IsAny<KafkaMessageEnvelope<RentalReminderDuePayload>>(),
            Moq.It.IsAny<CancellationToken>()))
            .Returns<string, string, KafkaMessageEnvelope<RentalReminderDuePayload>, CancellationToken>((topic, key, env, ct) =>
            {
                if (capturedMessageId1 == Guid.Empty)
                    capturedMessageId1 = env.MessageId;
                else
                    capturedMessageId2 = env.MessageId;

                return Task.FromResult(Result.Success(new ProducerEnqueueResult(env.MessageId, false, env.MessageType, topic, key)));
            });

        var producer = new RentalEventProducer(
            mockWriter.Object,
            Options.Create(new KafkaOptions { RentalEventsTopic = "rental.events.v1" }),
            timeProvider);

        var res1 = await producer.EnqueueRentalReminderDueAsync(payload, correlationId: "corr-abc");
        Assert.True(res1.IsSuccess);

        var res2 = await producer.EnqueueRentalReminderDueAsync(payload, correlationId: "corr-xyz");
        Assert.True(res2.IsSuccess);

        Assert.Equal(capturedMessageId1, capturedMessageId2);

        string expectedLogicalEntityId = $"{payload.RentalItemId:D}:{payload.ReminderType}:{payload.ReminderDate:yyyy-MM-dd}";
        Guid expectedMessageId = OutboxMessageIdGenerator.ComputeDeterministicId(
            KafkaContractSpec.Producers.RentalService,
            KafkaContractSpec.EventTypes.RentalReminderDue,
            expectedLogicalEntityId);

        Assert.Equal(expectedMessageId, capturedMessageId1);
    }

    [Fact]
    public async Task RentalReminderScheduleRepository_LeaseChecks_EnforceTokenAndExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        Guid scheduleId;
        Guid correctToken = Guid.NewGuid();
        Guid wrongToken = Guid.NewGuid();

        await using (var db = CreateDbContext())
        {
            var schedule = RentalReminderSchedule.Create(
                orderId, itemId, RentalReminderScheduleType.Handover, 1, now.AddMinutes(-5), now.AddDays(-2));
            schedule.Claim("worker-1", correctToken, now.AddMinutes(5), now);
            db.RentalReminderSchedules.Add(schedule);
            await db.SaveChangesAsync();
            scheduleId = schedule.Id;
        }

        await using (var db = CreateDbContext())
        {
            var repo = new RentalReminderScheduleRepository(db);

            bool advancedWrong = await repo.AdvanceScheduleWithLeaseCheckAsync(
                scheduleId, wrongToken, DateOnly.FromDateTime(now.Date), now, now.AddDays(1), false, false);
            Assert.False(advancedWrong);

            bool advancedExpired = await repo.AdvanceScheduleWithLeaseCheckAsync(
                scheduleId, correctToken, DateOnly.FromDateTime(now.Date), now.AddMinutes(10), now.AddDays(1), false, false);
            Assert.False(advancedExpired);

            bool failWrong = await repo.FailScheduleWithLeaseCheckAsync(
                scheduleId, wrongToken, now.AddMinutes(1), now);
            Assert.False(failWrong);

            bool releaseWrong = await repo.ReleaseLeaseWithTokenAsync(
                scheduleId, wrongToken, null, now);
            Assert.False(releaseWrong);

            bool completeWrong = await repo.CompleteScheduleWithLeaseCheckAsync(
                scheduleId, wrongToken, now);
            Assert.False(completeWrong);

            bool releaseCorrect = await repo.ReleaseLeaseWithTokenAsync(
                scheduleId, correctToken, null, now);
            Assert.True(releaseCorrect);
        }
    }

    [Fact]
    public async Task RentalReminderScheduleRepository_ClaimDueBatchAsync_ReturnsReminderClaimDescriptorWithLeaseExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        Guid scheduleId;

        await using (var db = CreateDbContext())
        {
            var schedule = RentalReminderSchedule.Create(
                orderId, itemId, RentalReminderScheduleType.Handover, 2, now.AddMinutes(-5), now.AddDays(-2));
            db.RentalReminderSchedules.Add(schedule);
            await db.SaveChangesAsync();
            scheduleId = schedule.Id;
        }

        await using (var db = CreateDbContext())
        {
            var repo = new RentalReminderScheduleRepository(db);
            var claims = await repo.ClaimDueBatchAsync("unit-worker", TimeSpan.FromMinutes(3), 10);

            var claim = Assert.Single(claims);
            Assert.Equal(scheduleId, claim.ScheduleId);
            Assert.Equal(orderId, claim.OrderId);
            Assert.Equal(itemId, claim.ItemId);
            Assert.Equal(RentalReminderScheduleType.Handover, claim.ScheduleType);
            Assert.NotEqual(Guid.Empty, claim.LeaseToken);
            Assert.True(claim.LeaseExpiry > now);
            Assert.Equal(claim.LeaseExpiry, claim.LeasedUntil);
        }
    }
}
