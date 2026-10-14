using System.Text.Json;
using Dprental.RentalService.Application.Kafka.Workers;
using Dprental.RentalService.Application.Kafka.Consumers;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.IServices.Integration;
using Dprental.RentalService.Application.IServices.Payments;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Repositories;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Producers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Dprental.RentalService.Api.Tests.Domain;

namespace Dprental.RentalService.Api.Tests.Background;

public sealed class InboxProcessingWorkerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 8, 8, 15, 30, TimeSpan.Zero);

    private static RentalServiceDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new RentalServiceDbContext(options);
    }

    private sealed class StubPaymentWorkflow : IPaymentWorkflowService
    {
        public Func<KafkaMessageEnvelope<InvoicePaidPayload>, Task<Result>>? Handler { get; set; }

        public Task<Result> ApplyInvoicePaidStagedAsync(
            RentalOrder order,
            KafkaMessageEnvelope<InvoicePaidPayload> envelope,
            RentalInboxMessage inboxMessage,
            CancellationToken cancellationToken = default) =>
            Handler != null ? Handler(envelope) : Task.FromResult(Result.Success());

        public Task<Result> ProcessInvoicePaidAsync(KafkaMessageEnvelope<InvoicePaidPayload> envelope, CancellationToken cancellationToken = default) =>
            Handler != null ? Handler(envelope) : Task.FromResult(Result.Success());
    }

    private sealed class StubRefundConsumer : IRefundCompletedConsumer
    {
        public Task<Result> ApplyRefundCompletedStagedAsync(
            RentalOrder order,
            KafkaMessageEnvelope<RefundCompletedPayload> envelope,
            RentalInboxMessage inboxMessage,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> ProcessRefundCompletedAsync(KafkaMessageEnvelope<RefundCompletedPayload> envelope, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }

    [Fact]
    public async Task InboxWorker_SuccessfulProcessing_MarksProcessed()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);

        Guid orderId = Guid.NewGuid();
        var order = RentalOrder.Create(
            orderId, "ORD-001", Guid.NewGuid(), "VND", "Standard", "Renter", "0123456789", "Address", null,
            "ORIGINAL_PAYMENT_SOURCE", "v1.0", "DOC-001", "HASH-001", "OP-001", FixedNow,
            new[] { new InitialRentalItemDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), "Listing", null, "Pickup", 500_000m, 1_000_000m, "GRP-001", new[] { new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) }, TestListingSnapshots.Default) }).Value;
        db.RentalOrders.Add(order);

        var payload = new InvoicePaidPayload(orderId, order.RenterId, "INV-001", "PAY-001", order.TotalPayableAmount, "VND", FixedNow);
        var envelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
            Guid.NewGuid(), "InvoicePaid", 1, "payment-service", orderId.ToString("D"), FixedNow, "corr", null, payload);

        var inboxMsg = new RentalInboxMessage(
            Guid.NewGuid(), envelope.MessageId, "InvoicePaid", "payment-service", orderId.ToString("D"), null, null,
            JsonSerializer.Serialize(envelope), FixedNow);
        db.RentalInboxMessages.Add(inboxMsg);
        await db.SaveChangesAsync();
        var services = new ServiceCollection();
        services.AddRentalServiceDomainRepositories("Host=localhost;Database=test;Username=test");
        services.RemoveAll<DbContextOptions<RentalServiceDbContext>>();
        services.RemoveAll<IDbContextOptionsConfiguration<RentalServiceDbContext>>();
        services.AddDbContext<RentalServiceDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddLogging();
        services.AddSingleton<IOrderLockManager, Dprental.RentalService.Api.Tests.TestHelpers.FakeOrderLockManager>();
        services.AddSingleton<IPaymentWorkflowService>(new StubPaymentWorkflow());
        services.AddSingleton<IRefundCompletedConsumer>(new StubRefundConsumer());
        services.AddScoped<IKafkaOutboxWriter, KafkaOutboxWriter>();
        services.AddScoped<IPaymentEventsDispatcher, PaymentEventsDispatcher>();
        services.AddSingleton(Options.Create(new KafkaOptions { Enabled = true }));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var worker = new InboxProcessingWorker(scopeFactory, Options.Create(new KafkaOptions { Enabled = true }), NullLogger<InboxProcessingWorker>.Instance);
        int processed = await worker.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(1, processed);

        await using var verifyDb = CreateDbContext(dbName);
        var reloaded = await verifyDb.RentalInboxMessages.FirstAsync(m => m.Id == inboxMsg.Id);
        Assert.Equal("Processed", reloaded.Status);
    }

    [Fact]
    public async Task InboxWorker_RetryableError_IncrementsRetryAndSchedulesNext()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);

        Guid orderId = Guid.NewGuid();
        var order = RentalOrder.Create(
            orderId, "ORD-002", Guid.NewGuid(), "VND", "Standard", "Renter", "0123456789", "Address", null,
            "ORIGINAL_PAYMENT_SOURCE", "v1.0", "DOC-001", "HASH-001", "OP-002", FixedNow,
            new[] { new InitialRentalItemDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), "Listing", null, "Pickup", 500_000m, 1_000_000m, "GRP-002", new[] { new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) }, TestListingSnapshots.Default) }).Value;
        db.RentalOrders.Add(order);

        var payload = new InvoicePaidPayload(orderId, order.RenterId, "INV-002", "PAY-002", order.TotalPayableAmount, "VND", FixedNow);
        var envelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
            Guid.NewGuid(), "InvoicePaid", 1, "payment-service", orderId.ToString("D"), FixedNow, "corr", null, payload);

        var inboxMsg = new RentalInboxMessage(
            Guid.NewGuid(), envelope.MessageId, "InvoicePaid", "payment-service", orderId.ToString("D"), null, null,
            JsonSerializer.Serialize(envelope), FixedNow);
        db.RentalInboxMessages.Add(inboxMsg);
        await db.SaveChangesAsync();
        var paymentWorkflow = new StubPaymentWorkflow
        {
            Handler = _ => Task.FromResult(Result.Failure(Error.Failure("INVOICE_NOT_ATTACHED", "Invoice not attached yet")))
        };

        var services = new ServiceCollection();
        services.AddRentalServiceDomainRepositories("Host=localhost;Database=test;Username=test");
        services.RemoveAll<DbContextOptions<RentalServiceDbContext>>();
        services.RemoveAll<IDbContextOptionsConfiguration<RentalServiceDbContext>>();
        services.AddDbContext<RentalServiceDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddLogging();
        services.AddSingleton<IOrderLockManager, Dprental.RentalService.Api.Tests.TestHelpers.FakeOrderLockManager>();
        services.AddSingleton<IPaymentWorkflowService>(paymentWorkflow);
        services.AddSingleton<IRefundCompletedConsumer>(new StubRefundConsumer());
        services.AddScoped<IKafkaOutboxWriter, KafkaOutboxWriter>();
        services.AddScoped<IPaymentEventsDispatcher, PaymentEventsDispatcher>();
        services.AddSingleton(Options.Create(new KafkaOptions { Enabled = true }));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var worker = new InboxProcessingWorker(scopeFactory, Options.Create(new KafkaOptions { Enabled = true }), NullLogger<InboxProcessingWorker>.Instance);
        int processed = await worker.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(0, processed);

        await using var verifyDb = CreateDbContext(dbName);
        var reloaded = await verifyDb.RentalInboxMessages.FirstAsync(m => m.Id == inboxMsg.Id);
        Assert.Equal("Failed", reloaded.Status);
        Assert.Equal(1, reloaded.RetryCount);
        Assert.NotNull(reloaded.NextRetryAt);
    }
}
