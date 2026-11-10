using Dprental.RentalService.Application.IServices.Reminders;
using Dprental.RentalService.Application.Services.Reminders;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Api.Tests.Orders;
using Dprental.RentalService.Api.Tests.Checkout;
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
            Assert.Equal(0, updatedSchedule.RemainingUnitCount);
            Assert.Null(updatedSchedule.NextTriggerAt);
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

    [Fact]
    public async Task RentalReminderWorker_LoopRecovery_WhenClaimThrowsTwice_RecoversAndContinues()
    {
        int claimAttempts = 0;
        var tcsSuccess = new TaskCompletionSource<bool>();

        var repoMock = new Mock<IRentalReminderScheduleRepository>();
        repoMock.Setup(r => r.ClaimDueBatchAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<string, TimeSpan, int, CancellationToken>((workerId, leaseDuration, batchSize, ct) =>
            {
                claimAttempts++;
                if (claimAttempts <= 2)
                {
                    throw new InvalidOperationException($"Simulated transient DB error attempt {claimAttempts}");
                }
                tcsSuccess.TrySetResult(true);
                return Task.FromResult<IReadOnlyList<ReminderClaimDescriptor>>(Array.Empty<ReminderClaimDescriptor>());
            });

        var services = new ServiceCollection();
        services.AddSingleton(repoMock.Object);
        var sp = services.BuildServiceProvider();

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(() =>
        {
            var scopeMock = new Mock<IServiceScope>();
            scopeMock.Setup(s => s.ServiceProvider).Returns(sp);
            return scopeMock.Object;
        });

        var options = Options.Create(new RentalReminderOptions
        {
            BatchSize = 10,
            ScanIntervalSeconds = 1,
            MinClaimFailureBackoffSeconds = 0,
            MaxClaimFailureBackoffSeconds = 1
        });
        var worker = new RentalReminderWorker(
            scopeFactoryMock.Object,
            options,
            NullLogger<RentalReminderWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StartAsync(cts.Token);

        // Wait for attempt 3 to be reached
        var finished = await Task.WhenAny(tcsSuccess.Task, Task.Delay(2000, cts.Token));
        await worker.StopAsync(CancellationToken.None);

        Assert.True(tcsSuccess.Task.IsCompletedSuccessfully, $"Expected worker to recover after 2 failures and execute attempt 3, but only reached {claimAttempts} attempts.");
        Assert.True(claimAttempts >= 3, $"Expected at least 3 attempts, got {claimAttempts}");
    }

    [Fact]
    public async Task RentalReminderWorker_WhenOneOrderFails_DoesNotBlockOtherOrdersInBatch()
    {
        Guid order1Id = Guid.NewGuid();
        Guid order2Id = Guid.NewGuid();
        Guid item1Id = Guid.NewGuid();
        Guid item2Id = Guid.NewGuid();

        var desc1 = new ReminderClaimDescriptor(Guid.NewGuid(), order1Id, item1Id, RentalReminderScheduleType.Handover, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));
        var desc2 = new ReminderClaimDescriptor(Guid.NewGuid(), order2Id, item2Id, RentalReminderScheduleType.Handover, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));

        var repoMock = new Mock<IRentalReminderScheduleRepository>();
        repoMock.Setup(r => r.ClaimDueBatchAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReminderClaimDescriptor> { desc1, desc2 });

        var processorMock = new Mock<IRentalReminderBatchProcessor>();
        // Order 1 throws unhandled exception
        processorMock.Setup(p => p.ProcessOrderReminderGroupAsync(order1Id, It.IsAny<IReadOnlyList<ReminderClaimDescriptor>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Order 1 failed unexpectedly"));
        // Order 2 succeeds
        processorMock.Setup(p => p.ProcessOrderReminderGroupAsync(order2Id, It.IsAny<IReadOnlyList<ReminderClaimDescriptor>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessOrderGroupResult(1, 1, 0, 0));

        var services = new ServiceCollection();
        services.AddSingleton(repoMock.Object);
        services.AddSingleton(processorMock.Object);
        var sp = services.BuildServiceProvider();

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(() =>
        {
            var scopeMock = new Mock<IServiceScope>();
            scopeMock.Setup(s => s.ServiceProvider).Returns(sp);
            return scopeMock.Object;
        });

        var worker = new RentalReminderWorker(
            scopeFactoryMock.Object,
            Options.Create(new RentalReminderOptions { BatchSize = 10 }),
            NullLogger<RentalReminderWorker>.Instance);

        var result = await worker.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(2, result.Claimed);
        Assert.Equal(2, result.Processed);
        Assert.Equal(1, result.NoticesSent);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task ProcessOrderReminderGroupAsync_WhenItemPastD30WithEmptyAssignments_CompletesScheduleWithoutPermanentFailureOrRetry()
    {
        var now = new DateTimeOffset(2026, 11, 9, 9, 0, 0, VnOffset);
        var timeProvider = new FixedTimeProvider(now);

        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        Guid scheduleId;

        // Target end date is 31 days ago (WindowExpired)
        DateOnly expiredEndDate = DateOnly.FromDateTime(now.AddDays(-31).DateTime);

        await using (var db = CreateDbContext())
        {
            var order = OrderTestData.Paid(
                OrderTestData.Order(_renterId, "DH-2026-REM-EXPIRED", now.AddDays(-60),
                    OrderTestData.Line(_supplierId, "Máy khoan", "Khoan", "Bosch", 1, 1000m, 5000m)),
                now.AddDays(-60));
            typeof(RentalOrder).GetProperty(nameof(RentalOrder.Id))!.SetValue(order, orderId);
            var item = order.Items.First();
            typeof(RentalItem).GetProperty(nameof(RentalItem.Id))!.SetValue(item, itemId);
            typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(item, RentalItemStatus.InRental);
            typeof(RentalItem).GetProperty(nameof(RentalItem.ExpectedEndDate))!.SetValue(item, expiredEndDate);

            // Clear assignments to simulate corrupted / missing assignment data
            typeof(RentalItem).GetField("_unitAssignments", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(item, new List<RentalUnitAssignment>());

            db.RentalOrders.Add(order);

            var schedule = RentalReminderSchedule.Create(
                orderId, itemId, RentalReminderScheduleType.Return, 1, now.AddMinutes(-5), now.AddDays(-31));
            db.RentalReminderSchedules.Add(schedule);
            await db.SaveChangesAsync();
            scheduleId = schedule.Id;
        }

        await using (var db = CreateDbContext())
        {
            var repo = new RentalReminderScheduleRepository(db);
            var lockManager = new FakeOrderLockManager();
            var producer = new FakeRentalEventProducer();
            var processor = new RentalReminderBatchProcessor(
                repo,
                lockManager,
                producer,
                unitOfWork: db,
                timeProvider: timeProvider,
                logger: NullLogger<RentalReminderBatchProcessor>.Instance);

            var claims = await repo.ClaimDueBatchAsync("test-worker", TimeSpan.FromMinutes(2), 10);
            var descriptor = Assert.Single(claims);

            var result = await processor.ProcessOrderReminderGroupAsync(orderId, [descriptor]);

            // Must complete schedule because it is WindowExpired!
            // Must NOT fail with PermanentFailure!
            Assert.Equal(1, result.Completed);
            Assert.Equal(0, result.Failed);
            Assert.Equal(0, result.NoticesSent);
            Assert.Equal(ReminderItemProcessOutcome.Succeeded, result.ItemResults[0].Outcome);

            var updatedSchedule = await db.RentalReminderSchedules.FirstAsync(s => s.Id == scheduleId);
            Assert.Equal(RentalReminderScheduleStatus.Completed, updatedSchedule.Status);
            Assert.Null(updatedSchedule.NextTriggerAt);
        }
    }

    [Fact]
    public async Task ProcessOrderReminderGroupAsync_WhenItemAtD30After8AmWithValidAssignments_SendsFinalNoticeAndMarksCompleted()
    {
        var now = new DateTimeOffset(2026, 11, 9, 9, 0, 0, VnOffset);
        var timeProvider = new FixedTimeProvider(now);

        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        Guid scheduleId;

        // Target end date is exactly 30 days ago (D+30)
        DateOnly targetEndDate = DateOnly.FromDateTime(now.AddDays(-30).DateTime);

        await using (var db = CreateDbContext())
        {
            var order = OrderTestData.Paid(
                OrderTestData.Order(_renterId, "DH-2026-REM-D30", now.AddDays(-60),
                    OrderTestData.Line(_supplierId, "Máy chiếu", "Chiếu", "Sony", 1, 2000m, 10000m)),
                now.AddDays(-60));
            typeof(RentalOrder).GetProperty(nameof(RentalOrder.Id))!.SetValue(order, orderId);
            var item = order.Items.First();
            typeof(RentalItem).GetProperty(nameof(RentalItem.Id))!.SetValue(item, itemId);
            typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(item, RentalItemStatus.InRental);
            typeof(RentalItem).GetProperty(nameof(RentalItem.ExpectedEndDate))!.SetValue(item, targetEndDate);

            db.RentalOrders.Add(order);

            var schedule = RentalReminderSchedule.Create(
                orderId, itemId, RentalReminderScheduleType.Return, 1, now.AddMinutes(-5), now.AddDays(-30));
            db.RentalReminderSchedules.Add(schedule);
            await db.SaveChangesAsync();
            scheduleId = schedule.Id;
        }

        await using (var db = CreateDbContext())
        {
            var repo = new RentalReminderScheduleRepository(db);
            var lockManager = new FakeOrderLockManager();
            var producer = new FakeRentalEventProducer();
            var processor = new RentalReminderBatchProcessor(
                repo,
                lockManager,
                producer,
                unitOfWork: db,
                timeProvider: timeProvider,
                logger: NullLogger<RentalReminderBatchProcessor>.Instance);

            var claims = await repo.ClaimDueBatchAsync("test-worker", TimeSpan.FromMinutes(2), 10);
            var descriptor = Assert.Single(claims);

            var result = await processor.ProcessOrderReminderGroupAsync(orderId, [descriptor]);

            // D+30 must send final notification AND complete after send
            Assert.Equal(1, result.NoticesSent);
            Assert.Equal(1, result.Completed);
            Assert.Equal(0, result.Failed);
            Assert.Equal(ReminderItemProcessOutcome.Succeeded, result.ItemResults[0].Outcome);

            var updatedSchedule = await db.RentalReminderSchedules.FirstAsync(s => s.Id == scheduleId);
            Assert.Equal(RentalReminderScheduleStatus.Completed, updatedSchedule.Status);
            Assert.NotNull(updatedSchedule.LastNotifiedDate);
        }
    }

    [Fact]
    public async Task ProcessOrderReminderGroupAsync_WhenItemAtD29WithCorruptedAssignments_RecordsPermanentFailure()
    {
        var now = new DateTimeOffset(2026, 11, 9, 9, 0, 0, VnOffset);
        var timeProvider = new FixedTimeProvider(now);

        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        Guid scheduleId;

        // Target end date is 29 days ago (within window, SendToday)
        DateOnly targetEndDate = DateOnly.FromDateTime(now.AddDays(-29).DateTime);

        await using (var db = CreateDbContext())
        {
            var order = OrderTestData.Paid(
                OrderTestData.Order(_renterId, "DH-2026-REM-D29", now.AddDays(-60),
                    OrderTestData.Line(_supplierId, "Flycam", "Drone", "DJI", 1, 3000m, 15000m)),
                now.AddDays(-60));
            typeof(RentalOrder).GetProperty(nameof(RentalOrder.Id))!.SetValue(order, orderId);
            var item = order.Items.First();
            typeof(RentalItem).GetProperty(nameof(RentalItem.Id))!.SetValue(item, itemId);
            typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(item, RentalItemStatus.InRental);
            typeof(RentalItem).GetProperty(nameof(RentalItem.ExpectedEndDate))!.SetValue(item, targetEndDate);

            // Corrupted empty assignments
            typeof(RentalItem).GetField("_unitAssignments", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(item, new List<RentalUnitAssignment>());

            db.RentalOrders.Add(order);

            var schedule = RentalReminderSchedule.Create(
                orderId, itemId, RentalReminderScheduleType.Return, 1, now.AddMinutes(-5), now.AddDays(-29));
            db.RentalReminderSchedules.Add(schedule);
            await db.SaveChangesAsync();
            scheduleId = schedule.Id;
        }

        await using (var db = CreateDbContext())
        {
            var repo = new RentalReminderScheduleRepository(db);
            var lockManager = new FakeOrderLockManager();
            var producer = new FakeRentalEventProducer();
            var processor = new RentalReminderBatchProcessor(
                repo,
                lockManager,
                producer,
                unitOfWork: db,
                timeProvider: timeProvider,
                logger: NullLogger<RentalReminderBatchProcessor>.Instance);

            var claims = await repo.ClaimDueBatchAsync("test-worker", TimeSpan.FromMinutes(2), 10);
            var descriptor = Assert.Single(claims);

            var result = await processor.ProcessOrderReminderGroupAsync(orderId, [descriptor]);

            // D+29 with corrupted assignments must result in PermanentFailure, not completed
            Assert.Equal(0, result.NoticesSent);
            Assert.Equal(0, result.Completed);
            Assert.Equal(1, result.Failed);
            Assert.Equal(ReminderItemProcessOutcome.PermanentFailure, result.ItemResults[0].Outcome);

            var updatedSchedule = await db.RentalReminderSchedules.FirstAsync(s => s.Id == scheduleId);
            Assert.Equal(RentalReminderScheduleStatus.Active, updatedSchedule.Status);
        }
    }

    [Fact]
    public async Task ProcessOrderReminderGroupAsync_WhenCompleteFailsDueToLostLease_DoesNotIncrementCompleted()
    {
        var now = new DateTimeOffset(2026, 11, 9, 9, 0, 0, VnOffset);
        var timeProvider = new FixedTimeProvider(now);

        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();

        // Expired date
        DateOnly expiredEndDate = DateOnly.FromDateTime(now.AddDays(-35).DateTime);

        var mockRepo = new Mock<IRentalReminderScheduleRepository>();
        var desc = new ReminderClaimDescriptor(Guid.NewGuid(), orderId, itemId, RentalReminderScheduleType.Return, Guid.NewGuid(), now.AddMinutes(2));

        var item = new ReminderItemProjection(itemId, Guid.NewGuid(), RentalItemStatus.InRental, expiredEndDate.AddDays(-5), expiredEndDate, 1, false, [], [], []);
        var order = new ReminderOrderProjection(orderId, RentalOrderStatus.Paid, "ORD-LOST-LEASE", Guid.NewGuid(), 1, [item]);

        mockRepo.Setup(r => r.LockSchedulesForUpdateAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([desc.ScheduleId]);
        mockRepo.Setup(r => r.GetScheduleStatesAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, ReminderScheduleStateProjection>
            {
                [desc.ScheduleId] = new ReminderScheduleStateProjection(desc.ScheduleId, RentalReminderScheduleStatus.Active, null, 0, 0, 1, desc.LeaseToken, desc.LeasedUntil)
            });
        mockRepo.Setup(r => r.GetOrderReminderProjectionAsync(orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(order);
        // Simulate lease lost during completion!
        mockRepo.Setup(r => r.CompleteScheduleWithLeaseCheckAsync(desc.ScheduleId, desc.LeaseToken, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var mockUnitOfWork = new Mock<IUnitOfWork>();
        var mockTx = new Mock<IUnitOfWorkTransaction>();
        mockUnitOfWork.Setup(u => u.BeginTransactionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(mockTx.Object);

        var processor = new RentalReminderBatchProcessor(
            mockRepo.Object,
            new FakeOrderLockManager(),
            new FakeRentalEventProducer(),
            unitOfWork: mockUnitOfWork.Object,
            timeProvider: timeProvider,
            logger: NullLogger<RentalReminderBatchProcessor>.Instance);

        var result = await processor.ProcessOrderReminderGroupAsync(orderId, [desc]);

        // Complete failed because lease was lost -> Completed must be 0!
        Assert.Equal(0, result.Completed);
        Assert.Equal(0, result.NoticesSent);
        Assert.Equal(ReminderItemProcessOutcome.LeaseLost, result.ItemResults[0].Outcome);
    }
}
