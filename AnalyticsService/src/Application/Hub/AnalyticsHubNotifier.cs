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
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(supplierUserIds);

        AnalyticsHubEnvelope<AnalyticsDataChangedPayload> envelope =
            new(Guid.NewGuid(), 1, _timeProvider.GetUtcNow(), new AnalyticsDataChangedPayload(scope));

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.SendTimeoutSeconds));

        try
        {
            await _hubContext.Clients.Group(AnalyticsHubSecurityConstants.AdminGroup)
                .SendAsync(AnalyticsHubEventNames.DataChanged, envelope, timeout.Token);

            // NCC chỉ nhận tin về dòng thuê của chính mình (ownership MVP: SupplierId = JWT.sub)
            string[] suppliers = supplierUserIds.Where(id => id != Guid.Empty).Distinct().Select(id => id.ToString("D")).ToArray();
            if (suppliers.Length > 0)
            {
                await _hubContext.Clients.Users(suppliers)
                    .SendAsync(AnalyticsHubEventNames.DataChanged, envelope, timeout.Token);
            }

            return Result.Success();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogSendFailed(_logger, null, scope);
            return Result.Failure(Error.Failure("AnalyticsHub.SendTimeout", "SignalR send timed out."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSendFailed(_logger, exception, scope);
            return Result.Failure(Error.Failure("AnalyticsHub.DeliveryFailed", "SignalR send failed."));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "AnalyticsHub notification for scope {Scope} was not delivered")]
    private static partial void LogSendFailed(ILogger logger, Exception? exception, string scope);
}
