using System.Text.Json;
using Dprental.RentalService.Api.Background;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Integration;
using Dprental.RentalService.Application.Payments;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

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
        public Func<PaymentEventEnvelope<InvoicePaidPayload>, Task<Result>>? Handler { get; set; }

        public Task<Result> ProcessInvoicePaidAsync(PaymentEventEnvelope<InvoicePaidPayload> envelope, CancellationToken cancellationToken = default) =>
            Handler != null ? Handler(envelope) : Task.FromResult(Result.Success());
    }

    private sealed class StubRefundConsumer : IRefundCompletedConsumer
    {
        public Task<Result> ProcessRefundCompletedAsync(PaymentEventEnvelope<RefundCompletedPayload> envelope, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }

    [Fact]
    public async Task InboxWorker_SuccessfulProcessing_MarksProcessed()
    {
        string dbName = Guid.NewGuid().ToString("N");
        await using var db = CreateDbContext(dbName);

        Guid orderId = Guid.NewGuid();
        var payload = new InvoicePaidPayload(orderId, Guid.NewGuid(), "INV-001", "PAY-001", 1_500_000m, "VND", FixedNow);
        var envelope = new PaymentEventEnvelope<InvoicePaidPayload>(
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
        services.AddSingleton<IPaymentWorkflowService>(new StubPaymentWorkflow());
        services.AddSingleton<IRefundCompletedConsumer>(new StubRefundConsumer());
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var worker = new InboxProcessingWorker(scopeFactory, new ConfigurationBuilder().Build(), NullLogger<InboxProcessingWorker>.Instance);
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
        var payload = new InvoicePaidPayload(orderId, Guid.NewGuid(), "INV-001", "PAY-001", 1_500_000m, "VND", FixedNow);
        var envelope = new PaymentEventEnvelope<InvoicePaidPayload>(
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
        services.AddSingleton<IPaymentWorkflowService>(paymentWorkflow);
        services.AddSingleton<IRefundCompletedConsumer>(new StubRefundConsumer());
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var worker = new InboxProcessingWorker(scopeFactory, new ConfigurationBuilder().Build(), NullLogger<InboxProcessingWorker>.Instance);
        int processed = await worker.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(0, processed);

        await using var verifyDb = CreateDbContext(dbName);
        var reloaded = await verifyDb.RentalInboxMessages.FirstAsync(m => m.Id == inboxMsg.Id);
        Assert.Equal("Failed", reloaded.Status);
        Assert.Equal(1, reloaded.RetryCount);
        Assert.NotNull(reloaded.NextRetryAt);
    }
}
