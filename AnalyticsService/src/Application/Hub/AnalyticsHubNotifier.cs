using Dprental.AnalyticsService.Application.Hub.Contracts;
using Dprental.AnalyticsService.Application.Hub.Options;
using Dprental.AnalyticsService.Application.Hub.Security;
using Dprental.AnalyticsService.Common;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.AnalyticsService.Application.Hub;

/// <summary>
/// Gửi tín hiệu "số liệu đã đổi" tới nhóm admins và tới các NCC liên quan, có timeout (giống PaymentHubNotifier).
/// </summary>
public sealed partial class AnalyticsHubNotifier : IAnalyticsHubNotifier
{
    private readonly IHubContext<AnalyticsHub> _hubContext;
    private readonly AnalyticsHubOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AnalyticsHubNotifier> _logger;

    public AnalyticsHubNotifier(
        IHubContext<AnalyticsHub> hubContext,
        IOptions<AnalyticsHubOptions> options,
        TimeProvider timeProvider,
        ILogger<AnalyticsHubNotifier> logger)
    {
        _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Result> NotifyDataChangedAsync(
        string scope, IReadOnlyCollection<Guid> supplierUserIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(supplierUserIds);
        AnalyticsHubEnvelope<AnalyticsDataChangedData> envelope =
            new(Guid.NewGuid(), 1, _timeProvider.GetUtcNow(), new AnalyticsDataChangedData(scope));

        bool adminFailed = false;
        bool supplierFailed = false;

        // 1. Gửi cho nhóm Admin với timeout riêng
        using (var adminCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            adminCts.CancelAfter(TimeSpan.FromSeconds(_options.SendTimeoutSeconds));
            try
            {
                await _hubContext.Clients.Group(AnalyticsHubSecurityConstants.AdminGroup)
                    .SendAsync(AnalyticsHubEventNames.DataChanged, envelope, adminCts.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // Cancellation do shutdown tiếp tục truyền ra ngoài
            }
            catch (OperationCanceledException)
            {
                adminFailed = true;
                LogSendFailed(_logger, null, scope, "Admins", "SendTimeout");
            }
            catch (Exception exception)
            {
                adminFailed = true;
                LogSendFailed(_logger, exception, scope, "Admins", "DeliveryFailed");
            }
        }

        // 2. Gửi cho các NCC với timeout riêng độc lập (lỗi admin không ngăn NCC, và ngược lại)
        string[] suppliers = supplierUserIds.Where(id => id != Guid.Empty).Distinct().Select(id => id.ToString("D")).ToArray();
        if (suppliers.Length > 0)
        {
            using (var supplierCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                supplierCts.CancelAfter(TimeSpan.FromSeconds(_options.SendTimeoutSeconds));
                try
                {
                    await _hubContext.Clients.Users(suppliers)
                        .SendAsync(AnalyticsHubEventNames.DataChanged, envelope, supplierCts.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw; // Cancellation do shutdown tiếp tục truyền ra ngoài
                }
                catch (OperationCanceledException)
                {
                    supplierFailed = true;
                    LogSendFailed(_logger, null, scope, "Suppliers", "SendTimeout");
                }
                catch (Exception exception)
                {
                    supplierFailed = true;
                    LogSendFailed(_logger, exception, scope, "Suppliers", "DeliveryFailed");
                }
            }
        }

        if (adminFailed || supplierFailed)
        {
            return Result.Failure(Error.Failure(
                "AnalyticsHub.DeliveryFailed",
                $"SignalR notification partially or fully failed (adminFailed: {adminFailed}, supplierFailed: {supplierFailed})."));
        }

        return Result.Success();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "AnalyticsHub notification for scope {Scope} was not delivered to {RecipientGroup} due to {Reason}")]
    private static partial void LogSendFailed(ILogger logger, Exception? exception, string scope, string recipientGroup, string reason);
}
