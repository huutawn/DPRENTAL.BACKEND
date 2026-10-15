using Dprental.PaymentService.Application.Background;
using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Unit;

public sealed class InvoiceExpiryWorkerTests
{
    [Fact]
    public async Task ProcessBatch_CallsInvoiceService_WithConfiguredBatchSize()
    {
        var fakeService = new FakeInvoiceService();

        var services = new ServiceCollection();
        services.AddScoped<IInvoiceService>(_ => fakeService);
        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Workers:InvoiceExpiry:IntervalSeconds"] = "10",
                ["Workers:InvoiceExpiry:BatchSize"] = "50"
            })
            .Build();

        var worker = new InvoiceExpiryWorker(
            scopeFactory,
            config,
            NullLogger<InvoiceExpiryWorker>.Instance);

        Assert.Equal(TimeSpan.FromSeconds(10), worker.Interval);
        Assert.Equal(50, worker.BatchSize);

        await worker.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(1, fakeService.ExpireBatchCallCount);
        Assert.Equal(50, fakeService.LastBatchSize);
    }

    [Fact]
    public async Task ExecuteAsync_RecoversFromException_AndContinuesNextTick()
    {
        var fakeService = new FakeInvoiceService { ShouldThrowOnFirstCall = true };

        var services = new ServiceCollection();
        services.AddScoped<IInvoiceService>(_ => fakeService);
        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Workers:InvoiceExpiry:IntervalSeconds"] = "5",
                ["Workers:InvoiceExpiry:BatchSize"] = "100"
            })
            .Build();

        var fakeTime = new FakeTimeProvider();
        var worker = new InvoiceExpiryWorker(
            scopeFactory,
            config,
            NullLogger<InvoiceExpiryWorker>.Instance,
            fakeTime);

        using var cts = new CancellationTokenSource();
        var workerTask = worker.StartAsync(cts.Token);

        // Tick 1: Throws exception -> Worker handles and does not crash
        fakeTime.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        // Tick 2: Succeeds
        fakeTime.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);

        Assert.True(fakeService.ExpireBatchCallCount >= 2,
            $"Expected at least 2 calls, got {fakeService.ExpireBatchCallCount}");
    }

    [Fact]
    public async Task ExecuteAsync_StopsCleanly_WhenCancelled()
    {
        var fakeService = new FakeInvoiceService();
        var services = new ServiceCollection();
        services.AddScoped<IInvoiceService>(_ => fakeService);
        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var config = new ConfigurationBuilder().Build();
        var fakeTime = new FakeTimeProvider();

        var worker = new InvoiceExpiryWorker(
            scopeFactory,
            config,
            NullLogger<InvoiceExpiryWorker>.Instance,
            fakeTime);

        using var cts = new CancellationTokenSource();
        var startTask = worker.StartAsync(cts.Token);

        cts.Cancel();
        var stopTask = worker.StopAsync(CancellationToken.None);

        await Task.WhenAll(startTask, stopTask);
        Assert.True(startTask.IsCompletedSuccessfully);
    }

    private sealed class FakeInvoiceService : IInvoiceService
    {
        public int ExpireBatchCallCount { get; private set; }
        public int LastBatchSize { get; private set; }
        public bool ShouldThrowOnFirstCall { get; set; }

        public Task<int> ExpirePendingInvoicesBatchAsync(int batchSize = 100, CancellationToken ct = default)
        {
            ExpireBatchCallCount++;
            LastBatchSize = batchSize;
            if (ShouldThrowOnFirstCall && ExpireBatchCallCount == 1)
            {
                throw new InvalidOperationException("Transient database error");
            }
            return Task.FromResult(2);
        }

        public Task<Result<CreateInvoiceResponse>> CreateInvoiceAsync(CreateInvoiceRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<Result<CancelInvoiceResponse>> CancelInvoiceAsync(string? routeInvoiceRef, CancelInvoiceRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<Result<GetInvoiceResponse>> GetInvoiceAsync(string invoiceRef, Guid callerRenterId, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }
}
