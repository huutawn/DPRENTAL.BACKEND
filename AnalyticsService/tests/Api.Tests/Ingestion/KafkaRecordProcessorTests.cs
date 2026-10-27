using Dprental.AnalyticsService.Application.Events;
using Dprental.AnalyticsService.Application.Hub;
using Dprental.AnalyticsService.Application.Hub.Contracts;
using Dprental.AnalyticsService.Application.Kafka.Configuration;
using Dprental.AnalyticsService.Application.Kafka.Consumers;
using Dprental.AnalyticsService.Application.Projections;
using Dprental.AnalyticsService.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests.Ingestion;

public sealed class KafkaRecordProcessorTests
{
    private static readonly KafkaRecord Record = new("rental.events.v1", 0, 42, null, "{}");

    [Fact]
    public async Task RetryLater_IsRetriedOnSameMessage_UntilItCanCommit()
    {
        ScriptedDispatcher dispatcher = new(DispatchOutcome.RetryLater, DispatchOutcome.RetryLater, DispatchOutcome.Processed);

        DispatchResult result = await CreateProcessor(dispatcher).ProcessAsync(Record, CancellationToken.None);

        Assert.Equal(DispatchOutcome.Processed, result.Outcome);
        Assert.Equal(3, dispatcher.Calls.Count);
        Assert.All(dispatcher.Calls, call => Assert.Equal(Record, call));
    }

    [Theory]
    [InlineData(DispatchOutcome.Duplicate)]
    [InlineData(DispatchOutcome.Skipped)]
    [InlineData(DispatchOutcome.DeadLettered)]
    public async Task NothingNewSaved_ReturnsOnce_AndDoesNotNotify(DispatchOutcome outcome)
    {
        RecordingNotifier notifier = new();
        ScriptedDispatcher dispatcher = new(outcome);

        DispatchResult result = await CreateProcessor(dispatcher, notifier).ProcessAsync(Record, CancellationToken.None);

        Assert.Equal(outcome, result.Outcome);
        Assert.Single(dispatcher.Calls);
        Assert.Empty(notifier.Scopes);
    }

    [Theory]
    [InlineData(RentalAnalyticsEventTypes.RentalOrderPaid, AnalyticsDataScopes.Rental)]
    [InlineData(PaymentAnalyticsEventTypes.RentalItemFinancialSettled, AnalyticsDataScopes.Finance)]
    public async Task Processed_NotifiesAfterSave_WithScopeFromMessageType(string messageType, string scope)
    {
        RecordingNotifier notifier = new();

        await CreateProcessor(new ScriptedDispatcher(DispatchOutcome.Processed) { MessageType = messageType }, notifier)
            .ProcessAsync(Record, CancellationToken.None);

        Assert.Equal([scope], notifier.Scopes);
    }

    [Fact]
    public async Task NotificationFailure_DoesNotFailProcessing()
    {
        DispatchResult result = await CreateProcessor(new ScriptedDispatcher(DispatchOutcome.Processed), new RecordingNotifier { Fail = true })
            .ProcessAsync(Record, CancellationToken.None);

        Assert.True(result.ShouldCommitOffset); // số liệu đã lưu → vẫn commit offset
    }

    [Fact]
    public async Task Shutdown_StopsRetrying()
    {
        using CancellationTokenSource shutdown = new();
        shutdown.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateProcessor(new ScriptedDispatcher(DispatchOutcome.RetryLater)).ProcessAsync(Record, shutdown.Token));
    }

    private static KafkaRecordProcessor CreateProcessor(IAnalyticsEventDispatcher dispatcher, RecordingNotifier? notifier = null)
    {
        ServiceCollection services = new();
        services.AddScoped(_ => dispatcher);
        services.AddScoped<ReadModelChangeSet>();
        ServiceProvider provider = services.BuildServiceProvider();

        return new KafkaRecordProcessor(
            provider.GetRequiredService<IServiceScopeFactory>(),
            notifier ?? new RecordingNotifier(),
            Options.Create(new AnalyticsKafkaOptions { RetryInitialDelayMilliseconds = 1, RetryMaxDelaySeconds = 1 }),
            TimeProvider.System,
            NullLogger<KafkaRecordProcessor>.Instance);
    }

    private sealed class RecordingNotifier : IAnalyticsHubNotifier
    {
        public bool Fail { get; init; }
        public List<string> Scopes { get; } = [];

        public Task<Result> NotifyDataChangedAsync(string scope, IReadOnlyCollection<Guid> supplierUserIds, CancellationToken cancellationToken = default)
        {
            Scopes.Add(scope);
            return Task.FromResult(Fail ? Result.Failure(Error.Failure("Test.SendFailed", "send failed")) : Result.Success());
        }
    }

    // Trả lần lượt các kết quả cho trước; hết danh sách thì lặp lại kết quả cuối
    private sealed class ScriptedDispatcher(params DispatchOutcome[] outcomes) : IAnalyticsEventDispatcher
    {
        public List<KafkaRecord> Calls { get; } = [];
        public string? MessageType { get; init; }

        public Task<DispatchResult> DispatchAsync(KafkaRecord record, CancellationToken cancellationToken = default)
        {
            Calls.Add(record);
            return Task.FromResult(new DispatchResult(outcomes[Math.Min(Calls.Count - 1, outcomes.Length - 1)], MessageType));
        }
    }
}
