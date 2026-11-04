using System.Text.Json;
using Dprental.NotificationService.Application.Clients;
using Dprental.NotificationService.Application.Templates;
using Dprental.NotificationService.Domain.Entities;
using Dprental.NotificationService.Application.Diagnostics;
using Dprental.NotificationService.Domain.IRepositories;
using Dprental.NotificationService.Application.Policies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dprental.NotificationService.Application.Email.Services;

public sealed class EmailDeliveryService : IEmailDeliveryService
{
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(5);
    public const int DefaultBatchSize = 1; // Sequential dispatch: claim 1 job to avoid holding batch while lease expires
    public static readonly TimeSpan DefaultDispatchTimeout = TimeSpan.FromSeconds(30);

    private readonly IDeliveryJobRepository _deliveryRepo;
    private readonly IPreferenceRepository _preferenceRepo;
    private readonly IAuthContactClient _authContactClient;
    private readonly INotificationTemplateRenderer _templateRenderer;
    private readonly IEmailSender _emailSender;
    private readonly IAggregateWatermarkRepository _watermarkRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<EmailDeliveryService> _logger;
    private readonly TimeSpan _leaseDuration;
    private readonly int _batchSize;
    private readonly TimeSpan _dispatchTimeout;

    public EmailDeliveryService(
        IDeliveryJobRepository deliveryRepo,
        IPreferenceRepository preferenceRepo,
        IAuthContactClient authContactClient,
        INotificationTemplateRenderer templateRenderer,
        IEmailSender emailSender,
        IAggregateWatermarkRepository watermarkRepo,
        IUnitOfWork unitOfWork,
        ILogger<EmailDeliveryService>? logger = null,
        TimeSpan? leaseDuration = null,
        int? batchSize = null,
        TimeSpan? dispatchTimeout = null)
    {
        _deliveryRepo = deliveryRepo ?? throw new ArgumentNullException(nameof(deliveryRepo));
        _preferenceRepo = preferenceRepo ?? throw new ArgumentNullException(nameof(preferenceRepo));
        _authContactClient = authContactClient ?? throw new ArgumentNullException(nameof(authContactClient));
        _templateRenderer = templateRenderer ?? throw new ArgumentNullException(nameof(templateRenderer));
        _emailSender = emailSender ?? throw new ArgumentNullException(nameof(emailSender));
        _watermarkRepo = watermarkRepo ?? throw new ArgumentNullException(nameof(watermarkRepo));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger = logger ?? NullLogger<EmailDeliveryService>.Instance;
        _leaseDuration = leaseDuration ?? DefaultLeaseDuration;
        _batchSize = batchSize ?? DefaultBatchSize;
        _dispatchTimeout = dispatchTimeout ?? DefaultDispatchTimeout;
    }

    public async Task<int> DeliverPendingJobsAsync(string workerId, CancellationToken cancellationToken = default)
    {
        var claimedJobs = await _deliveryRepo.ClaimPendingJobsAsync(workerId, _leaseDuration, _batchSize, cancellationToken);
        if (claimedJobs.Count == 0)
        {
            return 0;
        }

        _logger.LogInformation("EmailDeliveryService ({WorkerId}) đã claim {Count} jobs.", workerId, claimedJobs.Count);

        // 1. Tra cứu thông tin liên lạc từ AuthService trước transaction dispatch
        var recipientIds = claimedJobs.Select(j => j.RecipientUserId).Distinct().ToList();
        var contactsResult = await _authContactClient.GetUserContactsBatchAsync(recipientIds, cancellationToken);

        if (contactsResult.IsFailure)
        {
            _logger.LogWarning("Không thể lấy thông tin liên lạc từ AuthService: {Error}. Lên lịch retry cho các job đã claim.", contactsResult.Error.Message);
            var nowFailure = DateTimeOffset.UtcNow;
            foreach (var job in claimedJobs)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (string.IsNullOrEmpty(job.LeaseToken)) continue;

                if (job.AttemptCount < job.MaxAttempts)
                {
                    var nextAttempt = nowFailure.AddMinutes(Math.Pow(2, job.AttemptCount));
                    await _deliveryRepo.UpdateJobStatusAsync(
                        job.Id,
                        job.LeaseToken,
                        "RetryScheduled",
                        errorCode: contactsResult.Error.Code ?? "AUTH_CONTACT_FAILED",
                        errorDetail: contactsResult.Error.Message,
                        nextAttemptAt: nextAttempt,
                        resetDispatchStartedAt: true,
                        cancellationToken: cancellationToken);
                }
                else
                {
                    await _deliveryRepo.UpdateJobStatusAsync(
                        job.Id,
                        job.LeaseToken,
                        "Failed",
                        errorCode: contactsResult.Error.Code ?? "AUTH_CONTACT_FAILED",
                        errorDetail: $"Exceeded max retry attempts after AuthService contact resolution failures: {contactsResult.Error.Message}",
                        resetDispatchStartedAt: false,
                        cancellationToken: cancellationToken);
                }
            }

            return claimedJobs.Count;
        }

        var contactsMap = contactsResult.Value.ToDictionary(c => c.UserId);

        foreach (var job in claimedJobs)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (string.IsNullOrEmpty(job.LeaseToken)) continue;

            // Kiểm tra giới hạn số lần thử (Max 5 attempts)
            if (job.AttemptCount > job.MaxAttempts)
            {
                _logger.LogWarning("JobId={JobId} đã vượt quá số lần thử tối đa ({MaxAttempts}). Chuyển trạng thái sang Failed.", job.Id, job.MaxAttempts);
                await _deliveryRepo.UpdateJobStatusAsync(
                    job.Id,
                    job.LeaseToken,
                    "Failed",
                    errorCode: "MAX_ATTEMPTS_EXCEEDED",
                    errorDetail: $"Job exceeded max processing attempts ({job.MaxAttempts})",
                    resetDispatchStartedAt: false,
                    cancellationToken: cancellationToken);
                continue;
            }

            var notification = job.Notification;
            if (notification == null)
            {
                await _deliveryRepo.UpdateJobStatusAsync(
                    job.Id,
                    job.LeaseToken,
                    "Failed",
                    errorCode: "MISSING_NOTIFICATION",
                    errorDetail: "Job has no associated notification",
                    resetDispatchStartedAt: false,
                    cancellationToken: cancellationToken);
                continue;
            }

            // Kiểm tra sự tồn tại của người dùng từ AuthService
            if (!contactsMap.TryGetValue(job.RecipientUserId, out var contact))
            {
                _logger.LogWarning("Bỏ qua gửi email do không tìm thấy người dùng (user deleted): UserId={UserId}, JobId={JobId}", job.RecipientUserId, job.Id);
                await _deliveryRepo.UpdateJobStatusAsync(
                    job.Id,
                    job.LeaseToken,
                    "Suppressed",
                    errorCode: "USER_NOT_FOUND",
                    resetDispatchStartedAt: false,
                    cancellationToken: cancellationToken);
                continue;
            }

            // Kiểm tra trạng thái tài khoản và email xác thực từ AuthService
            if (!contact.IsActive || !contact.IsEmailVerified)
            {
                _logger.LogWarning("Bỏ qua gửi email do tài khoản không hoạt động hoặc email chưa xác thực: UserId={UserId}, JobId={JobId}", job.RecipientUserId, job.Id);
                await _deliveryRepo.UpdateJobStatusAsync(
                    job.Id,
                    job.LeaseToken,
                    "Suppressed",
                    errorCode: "INACTIVE_OR_UNVERIFIED_EMAIL",
                    resetDispatchStartedAt: false,
                    cancellationToken: cancellationToken);
                continue;
            }

            // Kiểm tra tùy chọn người dùng (User Preferences)
            var isEmailAllowed = await _preferenceRepo.IsEmailEnabledAsync(job.RecipientUserId, notification.Category, cancellationToken);
            if (!isEmailAllowed)
            {
                _logger.LogInformation("Bỏ qua gửi email do người dùng đã tắt preference: UserId={UserId}, Category={Category}, JobId={JobId}", job.RecipientUserId, notification.Category, job.Id);
                await _deliveryRepo.UpdateJobStatusAsync(
                    job.Id,
                    job.LeaseToken,
                    "Suppressed",
                    errorCode: "PREFERENCE_DISABLED",
                    resetDispatchStartedAt: false,
                    cancellationToken: cancellationToken);
                continue;
            }
            Dictionary<string, object?> renderData;
            if (string.IsNullOrWhiteSpace(notification.RenderDataJson))
            {
                renderData = new Dictionary<string, object?>();
            }
            else
            {
                try
                {
                    renderData = JsonSerializer.Deserialize<Dictionary<string, object?>>(notification.RenderDataJson)!;
                    if (renderData == null)
                    {
                        throw new JsonException("Deserialized render data dictionary is null.");
                    }
                }
                catch (Exception ex)
                {
                    await _deliveryRepo.UpdateJobStatusAsync(
                        job.Id,
                        job.LeaseToken,
                        "Failed",
                        errorCode: "CORRUPT_RENDER_DATA",
                        errorDetail: $"Malformed render data JSON: {ex.Message}",
                        resetDispatchStartedAt: false,
                        cancellationToken: cancellationToken);
                    continue;
                }
            }

            var renderedResult = _templateRenderer.Render(notification.NotificationType, renderData);
            if (renderedResult.IsFailure || string.IsNullOrWhiteSpace(renderedResult.Value.EmailSubject) || string.IsNullOrWhiteSpace(renderedResult.Value.EmailBody))
            {
                await _deliveryRepo.UpdateJobStatusAsync(
                    job.Id,
                    job.LeaseToken,
                    "Suppressed",
                    errorCode: "NO_EMAIL_TEMPLATE",
                    resetDispatchStartedAt: false,
                    cancellationToken: cancellationToken);
                continue;
            }

            // 2. Mở transaction ngắn: Advisory lock theo order ID + Watermark staleness check + DispatchStartedAt record
            var now = DateTimeOffset.UtcNow;
            if (job.Status != "Processing" ||
                string.IsNullOrEmpty(job.LeaseToken) ||
                job.LeaseExpiresAt == null ||
                job.LeaseExpiresAt <= now)
            {
                _logger.LogWarning("Bỏ qua gửi email do lease không hợp lệ hoặc đã hết hạn trước dispatch: JobId={JobId}, LeaseToken={LeaseToken}",
                    job.Id, job.LeaseToken);
                continue;
            }

            bool dispatchApproved = false;
            await using (var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken))
            {
                bool isStale = false;
                string? staleReason = null;

                var isFinancialNotification = notification.NotificationType is "REFUND_COMPLETED" or "PAYOUT_COMPLETED";

                if (!isFinancialNotification)
                {
                    AggregateStateProjection? orderAggregateState = null;
                    if (notification.OrderId.HasValue && notification.OrderId.Value != Guid.Empty)
                    {
                        await _watermarkRepo.AcquireAdvisoryLockAsync(notification.OrderId.Value, cancellationToken);

                        orderAggregateState = await _watermarkRepo.GetAggregateStateAsync(
                            NotificationWatermarkScope.RentalOrder,
                            notification.OrderId.Value,
                            cancellationToken);
                    }

                    AggregateStateProjection? currentAggregateState = null;
                    AggregateStateProjection? parentItemAggregateState = null;

                    if (string.Equals(notification.ResourceType, "RentalItem", StringComparison.OrdinalIgnoreCase) &&
                        Guid.TryParse(notification.ResourceId, out var itemId))
                    {
                        currentAggregateState = await _watermarkRepo.GetAggregateStateAsync(
                            NotificationWatermarkScope.RentalItem,
                            itemId,
                            cancellationToken);
                    }
                    else if (string.Equals(notification.ResourceType, "ReturnRequest", StringComparison.OrdinalIgnoreCase) &&
                             Guid.TryParse(notification.ResourceId, out var returnReqId))
                    {
                        currentAggregateState = await _watermarkRepo.GetAggregateStateAsync(
                            NotificationWatermarkScope.ReturnRequest,
                            returnReqId,
                            cancellationToken);

                        var parentItemId = notification.RentalItemId;
                        if (!parentItemId.HasValue)
                        {
                            var wmList = await _watermarkRepo.GetWatermarksForAggregateAsync(NotificationWatermarkScope.ReturnRequest, returnReqId, cancellationToken);
                            parentItemId = wmList.FirstOrDefault(w => w.ParentItemId.HasValue)?.ParentItemId;
                        }

                        if (parentItemId.HasValue && parentItemId.Value != Guid.Empty)
                        {
                            parentItemAggregateState = await _watermarkRepo.GetAggregateStateAsync(
                                NotificationWatermarkScope.RentalItem,
                                parentItemId.Value,
                                cancellationToken);
                        }
                    }
                    else if (notification.NotificationType == "RETURN_CONFIRMATION_OVERDUE_SUPPLIER")
                    {
                        var parentItemId = notification.RentalItemId;
                        if (parentItemId.HasValue && parentItemId.Value != Guid.Empty)
                        {
                            parentItemAggregateState = await _watermarkRepo.GetAggregateStateAsync(
                                NotificationWatermarkScope.RentalItem,
                                parentItemId.Value,
                                cancellationToken);
                        }
                    }
                    else
                    {
                        currentAggregateState = orderAggregateState;
                    }

                    var staleEval = StaleActionSuppressionPolicy.EvaluateWithParentState(
                        notification.NotificationType,
                        notification.EventVersion,
                        currentAggregateState,
                        parentItemAggregateState,
                        orderAggregateState);

                    if (staleEval.SuppressEmail)
                    {
                        isStale = true;
                        staleReason = staleEval.Reason;
                    }
                }
                if (isStale)
                {
                    _logger.LogWarning("Chặn gửi email do hành động hoặc đơn/thiết bị đã lỗi thời: JobId={JobId}, Reason={Reason}",
                        job.Id, staleReason);

                    await _deliveryRepo.UpdateJobStatusAsync(
                        job.Id,
                        job.LeaseToken,
                        "Suppressed",
                        errorCode: "STALE_ACTION_SUPPRESSED",
                        errorDetail: staleReason,
                        resetDispatchStartedAt: false,
                        cancellationToken: cancellationToken);

                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                    continue; // Suppressed: KHÔNG gọi SMTP!
                }

                // Nếu hợp lệ: ghi nhận DispatchStartedAt và commit transaction
                var dispatchRecorded = await _deliveryRepo.RecordDispatchStartAsync(job.Id, job.LeaseToken, DateTimeOffset.UtcNow, cancellationToken);
                if (!dispatchRecorded)
                {
                    _logger.LogWarning("Bỏ qua gửi email do RecordDispatchStartAsync thất bại (mất lease hoặc hết hạn): JobId={JobId}", job.Id);
                    await tx.RollbackAsync(cancellationToken);
                    continue;
                }

                await _unitOfWork.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                dispatchApproved = true;
            }

            if (!dispatchApproved)
            {
                continue;
            }

            // 3. Chỉ gọi SMTP sau khi bước trên thành công và transaction đã commit
            EmailSendResult sendResult;
            try
            {
                using var dispatchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                dispatchCts.CancelAfter(_dispatchTimeout);

                sendResult = await _emailSender.SendEmailAsync(
                    job.Id,
                    contact.Email,
                    renderedResult.Value.EmailSubject!,
                    renderedResult.Value.EmailBody!,
                    dispatchCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Gửi email cho JobId={JobId} bị timeout sau {Timeout}s. Phân loại là PostDataError / Uncertain.", job.Id, _dispatchTimeout.TotalSeconds);
                sendResult = new EmailSendResult(
                    Outcome: EmailSendOutcome.PostDataError,
                    ProviderMessageId: null,
                    ErrorCode: "SMTP_TIMEOUT_OR_DROPPED",
                    ErrorDetail: "Job dispatch timed out while sending via SMTP");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi ngoại lệ khi gửi email cho JobId={JobId}. Phân loại là PostDataError / Uncertain.", job.Id);
                sendResult = new EmailSendResult(
                    Outcome: EmailSendOutcome.PostDataError,
                    ProviderMessageId: null,
                    ErrorCode: "AMBIGUOUS_SEND_ERROR",
                    ErrorDetail: ex.Message);
            }

            // 4. Xử lý kết quả theo chính sách phân loại lỗi và lease fencing
            switch (sendResult.Outcome)
            {
                case EmailSendOutcome.Success:
                    await _deliveryRepo.UpdateJobStatusAsync(
                        job.Id,
                        job.LeaseToken,
                        "Succeeded",
                        providerMessageId: sendResult.ProviderMessageId,
                        resetDispatchStartedAt: false,
                        cancellationToken: cancellationToken);
                    break;

                case EmailSendOutcome.PreDataError:
                    // Reset DispatchStartedAt = null ONLY when safely retrying after 4xx or transient failure
                    if (job.AttemptCount < job.MaxAttempts)
                    {
                        var nextAttempt = DateTimeOffset.UtcNow.AddMinutes(Math.Pow(2, job.AttemptCount));
                        NotificationMetrics.DeliveryRetryCounter.Add(1);
                        await _deliveryRepo.UpdateJobStatusAsync(
                            job.Id,
                            job.LeaseToken,
                            "RetryScheduled",
                            errorCode: sendResult.ErrorCode,
                            errorDetail: sendResult.ErrorDetail,
                            nextAttemptAt: nextAttempt,
                            resetDispatchStartedAt: true,
                            cancellationToken: cancellationToken);
                    }
                    else
                    {
                        await _deliveryRepo.UpdateJobStatusAsync(
                            job.Id,
                            job.LeaseToken,
                            "Failed",
                            errorCode: "MAX_RETRIES_EXCEEDED",
                            errorDetail: sendResult.ErrorDetail,
                            resetDispatchStartedAt: false,
                            cancellationToken: cancellationToken);
                    }
                    break;

                case EmailSendOutcome.PostDataError:
                    // BẤT BIẾN: Lỗi xảy ra sau DATA hoặc timeout không được tự động blind retry!
                    _logger.LogWarning("JobId={JobId} gặp lỗi sau DATA phase hoặc timeout ({ErrorCode}). Chuyển trạng thái sang Uncertain để tránh gửi trùng lặp.", job.Id, sendResult.ErrorCode);
                    NotificationMetrics.UncertainDeliveryCounter.Add(1);
                    await _deliveryRepo.UpdateJobStatusAsync(
                        job.Id,
                        job.LeaseToken,
                        "Uncertain",
                        errorCode: sendResult.ErrorCode,
                        errorDetail: sendResult.ErrorDetail,
                        resetDispatchStartedAt: false, // KEEP DispatchStartedAt!
                        cancellationToken: cancellationToken);
                    break;

                case EmailSendOutcome.PermanentRejection:
                    await _deliveryRepo.UpdateJobStatusAsync(
                        job.Id,
                        job.LeaseToken,
                        "Failed",
                        errorCode: sendResult.ErrorCode,
                        errorDetail: sendResult.ErrorDetail,
                        resetDispatchStartedAt: false,
                        cancellationToken: cancellationToken);
                    break;
            }
        }

        return claimedJobs.Count;
    }
}
