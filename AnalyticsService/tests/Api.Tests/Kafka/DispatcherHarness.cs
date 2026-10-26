using System.Text.Json;
using Dprental.AnalyticsService.Application.Kafka.Consumers;
using Dprental.AnalyticsService.Application.Services.Projections;
using Dprental.AnalyticsService.Domain.IRepositories;
using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dprental.AnalyticsService.Api.Tests.Kafka;

/// <summary>
/// Dựng dispatcher thật trên DB InMemory. Mỗi lần Dispatch dùng một DbContext mới (giống worker
/// tạo scope riêng cho từng message), nhưng cùng một DB nên dữ liệu được giữ giữa các message.
/// </summary>
internal sealed class DispatcherHarness
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly DbContextOptions<AnalyticsServiceDbContext> _options;

    private long _nextOffset;

    // Mặc định: DB InMemory riêng. Test Postgres truyền options trỏ tới database tạm.
    public DispatcherHarness(DbContextOptions<AnalyticsServiceDbContext>? options = null)
    {
        _options = options ?? new DbContextOptionsBuilder<AnalyticsServiceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
    }

    public async Task<DispatchResult> DispatchAsync(KafkaRecord record, bool failOnSave = false)
    {
        await using AnalyticsServiceDbContext context = new(_options);
        IAnalyticsUnitOfWork unitOfWork = failOnSave ? new FailingUnitOfWork(context) : context;
        RentalFactRepository rentalFacts = new(context);
        ReadModelChangeSet changes = new();

        AnalyticsEventDispatcher dispatcher = new(
            new InboxRepository(context),
            new DeadLetterRepository(context),
            unitOfWork,
            new RentalOrderProjector(rentalFacts, changes),
            new RentalItemProjector(rentalFacts, changes),
            new FinancialProjector(new FinancialFactRepository(context)),
            TimeProvider.System,
            NullLogger<AnalyticsEventDispatcher>.Instance);

        return await dispatcher.DispatchAsync(record);
    }

    /// <summary>Mở DbContext mới để đọc kết quả.</summary>
    public AnalyticsServiceDbContext Read() => new(_options);

    /// <summary>Tạo message Kafka với offset tăng dần; payload là object bất kỳ, serialize kiểu camelCase.</summary>
    public KafkaRecord Message(
        string messageType, object payload, Guid? messageId = null, DateTimeOffset? occurredAt = null, string topic = "rental.events.v1")
    {
        string json = JsonSerializer.Serialize(new
        {
            messageId = messageId ?? Guid.NewGuid(),
            messageType,
            version = 1,
            producer = "test",
            occurredAt = occurredAt ?? DateTimeOffset.UtcNow,
            payload
        }, Json);

        return new KafkaRecord(topic, 0, _nextOffset++, json);
    }

    public KafkaRecord Raw(string value) => new("rental.events.v1", 0, _nextOffset++, value);

    // Giả lập DB sập đúng lúc lưu
    private sealed class FailingUnitOfWork(AnalyticsServiceDbContext context) : IAnalyticsUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated database outage");

        public void DiscardChanges() => context.DiscardChanges();
    }
}
