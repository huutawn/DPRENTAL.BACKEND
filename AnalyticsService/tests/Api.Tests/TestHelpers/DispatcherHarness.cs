using System.Text.Json;
using Dprental.AnalyticsService.Application.Events;
using Dprental.AnalyticsService.Application.Kafka.Configuration;
using Dprental.AnalyticsService.Application.Kafka.Consumers;
using Dprental.AnalyticsService.Application.Projections;
using Dprental.AnalyticsService.Domain.IRepositories;
using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dprental.AnalyticsService.Api.Tests.TestHelpers;

/// <summary>
/// Dispatcher thật trên một DB (InMemory hoặc PostgreSQL tạm). Mỗi lần Dispatch dùng DbContext mới,
/// giống worker tạo scope riêng cho từng message. Message Payment được ký proof thật.
/// </summary>
internal sealed class DispatcherHarness : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly DbContextOptions<AnalyticsServiceDbContext> _options;
    private long _nextOffset;

    public DispatcherHarness(DbContextOptions<AnalyticsServiceDbContext>? options = null)
    {
        _options = options ?? new DbContextOptionsBuilder<AnalyticsServiceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        Kafka = new AnalyticsKafkaOptions
        {
            ProofPublicKeys = { [TestProofSigner.KeyId] = Signer.PublicKeyBase64 }
        };
    }

    public TestProofSigner Signer { get; } = new();
    public AnalyticsKafkaOptions Kafka { get; }

    /// <summary>NCC đã chạm tới trong lần Dispatch gần nhất (để báo SignalR).</summary>
    public IReadOnlyCollection<Guid> LastTouchedSuppliers { get; private set; } = [];

    public async Task<DispatchResult> DispatchAsync(KafkaRecord record, bool failOnSave = false)
    {
        await using AnalyticsServiceDbContext context = new(_options);
        IAnalyticsUnitOfWork unitOfWork = failOnSave ? new FailingUnitOfWork(context) : context;
        RentalFactRepository rentalFacts = new(context);
        ReadModelChangeSet changes = new();

        AnalyticsEventDispatcher dispatcher = new(
            Options.Create(Kafka),
            new PaymentProofVerifier(Options.Create(Kafka), TimeProvider.System),
            new ProcessedRentalEventRepository(context),
            new DeadLetterRepository(context),
            unitOfWork,
            new RentalOrderProjector(rentalFacts, changes),
            new RentalItemProjector(rentalFacts, changes),
            new PaymentFinancialProjector(new PaymentFinancialEventRepository(context), changes),
            changes,
            rentalFacts,
            TimeProvider.System,
            NullLogger<AnalyticsEventDispatcher>.Instance);

        DispatchResult result = await dispatcher.DispatchAsync(record);
        LastTouchedSuppliers = changes.SupplierIds;
        return result;
    }

    public AnalyticsServiceDbContext Read() => new(_options);

    public KafkaRecord Rental(string messageType, object payload, Guid? messageId = null, DateTimeOffset? occurredAt = null) =>
        new(Kafka.RentalTopic, 0, _nextOffset++, null, Envelope(messageType, payload, messageId, occurredAt));

    /// <summary>Message Payment kèm proof hợp lệ (trừ khi truyền proof khác).</summary>
    public KafkaRecord Payment(string messageType, object payload, Guid? messageId = null, string key = "order-key", string? proof = null)
    {
        string value = Envelope(messageType, payload, messageId, occurredAt: null);
        return new KafkaRecord(Kafka.PaymentTopic, 0, _nextOffset++, key, value,
            proof ?? Signer.Sign(Kafka.PaymentTopic, key, value));
    }

    public KafkaRecord Raw(string topic, string value, string? key = null, string? proof = null) =>
        new(topic, 0, _nextOffset++, key, value, proof);

    public static string Envelope(string messageType, object payload, Guid? messageId = null, DateTimeOffset? occurredAt = null) =>
        JsonSerializer.Serialize(new
        {
            messageId = messageId ?? Guid.NewGuid(),
            messageType,
            version = KafkaEnvelope.SupportedVersion,
            producer = "test",
            aggregateId = Guid.NewGuid().ToString("D"),
            occurredAt = occurredAt ?? DateTimeOffset.UtcNow,
            payload
        }, Json);

    public void Dispose() => Signer.Dispose();

    // Giả lập DB sập đúng lúc lưu
    private sealed class FailingUnitOfWork(AnalyticsServiceDbContext context) : IAnalyticsUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated database outage");

        public void DiscardChanges() => context.DiscardChanges();
    }
}
