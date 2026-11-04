using System.Text.Json;
using Dprental.NotificationService.Application.Clients;
using Dprental.NotificationService.Application.Templates;
using Dprental.NotificationService.Domain.IRepositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dprental.NotificationService.Application.Email.Services;

public sealed class EmailDeliveryService : IEmailDeliveryService
{
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(5);
    public const int DefaultBatchSize = 10;
    public static readonly TimeSpan DefaultDispatchTimeout = TimeSpan.FromSeconds(30);

    private readonly IDeliveryJobRepository _deliveryRepo;
    private readonly IPreferenceRepository _preferenceRepo;
    private readonly IAuthContactClient _authContactClient;
    private readonly INotificationTemplateRenderer _templateRenderer;
    private readonly IEmailSender _emailSender;
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

        // Batch resolve contacts from AuthService
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
                        errorCode: "MAX_RETRIES_EXCEEDED",
                        errorDetail: contactsResult.Error.Message,
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

            // 1. Kiểm tra sự tồn tại của người dùng từ AuthService
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

            // 2. Kiểm tra trạng thái tài khoản và email xác thực từ AuthService
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

            // 3. Kiểm tra tùy chọn người dùng (User Preferences)
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

            // 4. Render email template
            Dictionary<string, object?> renderData;
            try
            {
                renderData = !string.IsNullOrWhiteSpace(notification.RenderDataJson)
                    ? JsonSerializer.Deserialize<Dictionary<string, object?>>(notification.RenderDataJson) ?? new Dictionary<string, object?>()
                    : new Dictionary<string, object?>();
            }
            catch
            {
                renderData = new Dictionary<string, object?>();
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

            // 5. Verify lease before dispatch: Status == "Processing", LeaseToken == leaseToken, LeaseExpiresAt != null && LeaseExpiresAt > now
            var now = DateTimeOffset.UtcNow;
            if (job.Status != "Processing" ||
                string.IsNullOrEmpty(job.LeaseToken) ||
                job.LeaseExpiresAt == null ||
                job.LeaseExpiresAt <= now)
            {
                _logger.LogWarning("Bỏ qua gửi email do lease không hợp lệ hoặc đã hết hạn trước dispatch: JobId={JobId}, LeaseToken={LeaseToken}, Status={Status}, LeaseExpiresAt={LeaseExpiresAt}",
                    job.Id, job.LeaseToken, job.Status, job.LeaseExpiresAt);
                continue;
            }

            // 6. Ghi nhận thời điểm bắt đầu dispatch bền vững trước khi gọi SMTP
            var dispatchRecorded = await _deliveryRepo.RecordDispatchStartAsync(job.Id, job.LeaseToken, now, cancellationToken);
            // Verify return value of RecordDispatchStartAsync: if false, abort dispatch immediately without calling SMTP!
            if (!dispatchRecorded)
            {
                _logger.LogWarning("Bỏ qua gửi email do RecordDispatchStartAsync thất bại (lease bị mất hoặc hết hạn): JobId={JobId}, LeaseToken={LeaseToken}",
                    job.Id, job.LeaseToken);
                continue;
            }

            // 7. Gửi Email qua SMTP adapter với timeout nhỏ hơn lease duration
            EmailSendResult sendResult;
            try
            {
                using var dispatchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                dispatchCts.CancelAfter(_dispatchTimeout);

                sendResult = await _emailSender.SendEmailAsync(
                    job.Id,
                    contact.Email,
                    renderedResult.Value.EmailSubject,
                    renderedResult.Value.EmailBody,
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

            // 8. Xử lý kết quả theo chính sách phân loại lỗi và lease fencing
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
                    await _deliveryRepo.UpdateJobStatusAsync(
                        job.Id,
                        job.LeaseToken,
                        "Uncertain",
                        errorCode: sendResult.ErrorCode,
                        errorDetail: sendResult.ErrorDetail,
                        resetDispatchStartedAt: false,
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
