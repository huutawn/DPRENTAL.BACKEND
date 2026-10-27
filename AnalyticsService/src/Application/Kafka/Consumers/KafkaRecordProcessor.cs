using Dprental.AnalyticsService.Application.Events;
using Dprental.AnalyticsService.Application.Hub;
using Dprental.AnalyticsService.Application.Hub.Contracts;
using Dprental.AnalyticsService.Application.Kafka.Configuration;
using Dprental.AnalyticsService.Application.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.AnalyticsService.Application.Kafka.Consumers;

/// <summary>
/// Xử lý một message cho đến khi được phép commit offset. Mỗi lần thử một DI scope mới (DbContext mới).
/// Lỗi tạm thời thì chờ tăng dần rồi thử lại CHÍNH message này, không bỏ qua để không mất dữ liệu.
/// Lưu thành công thì báo SignalR (sau khi DB đã commit). Tách khỏi worker để test không cần Kafka.
/// </summary>
public sealed partial class KafkaRecordProcessor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAnalyticsHubNotifier _notifier;
    private readonly AnalyticsKafkaOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<KafkaRecordProcessor> _logger;

    public KafkaRecordProcessor(
        IServiceScopeFactory scopeFactory,
        IAnalyticsHubNotifier notifier,
        IOptions<AnalyticsKafkaOptions> options,
        TimeProvider timeProvider,
        ILogger<KafkaRecordProcessor> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Trả về khi được phép commit offset (mọi kết quả trừ RetryLater). Chỉ dừng sớm khi ứng dụng tắt.</summary>
    public async Task<DispatchResult> ProcessAsync(KafkaRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        TimeSpan delay = TimeSpan.FromMilliseconds(_options.RetryInitialDelayMilliseconds);
        TimeSpan maxDelay = TimeSpan.FromSeconds(_options.RetryMaxDelaySeconds);

        while (true)
        {
            DispatchResult result;
            IReadOnlyCollection<Guid> touchedSuppliers = [];
            await using (AsyncServiceScope scope = _scopeFactory.CreateAsyncScope())
            {
                IAnalyticsEventDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IAnalyticsEventDispatcher>();
                result = await dispatcher.DispatchAsync(record, cancellationToken);

                // Lấy danh sách NCC trước khi đóng scope (ChangeSet thuộc scope)
                if (result.Outcome == DispatchOutcome.Processed)
                {
                    touchedSuppliers = scope.ServiceProvider.GetRequiredService<ReadModelChangeSet>().SupplierIds;
                }
            }

            if (result.ShouldCommitOffset)
            {
                if (result.Outcome == DispatchOutcome.Processed)
                {
                    // DB đã commit. Gửi lỗi chỉ được log trong notifier, không ảnh hưởng việc đọc Kafka.
                    string scope = PaymentAnalyticsEventTypes.IsFinancial(result.MessageType)
                        ? AnalyticsDataScopes.Finance
                        : AnalyticsDataScopes.Rental;
                    await _notifier.NotifyDataChangedAsync(scope, touchedSuppliers, cancellationToken);
                }

                return result;
            }

            LogRetryScheduled(_logger, record.Topic, record.Partition, record.Offset, delay.TotalMilliseconds);
            await Task.Delay(delay, _timeProvider, cancellationToken);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxDelay.Ticks));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retrying {Topic}[{Partition}]@{Offset} in {DelayMs} ms")]
    private static partial void LogRetryScheduled(ILogger logger, string topic, int partition, long offset, double delayMs);
}
