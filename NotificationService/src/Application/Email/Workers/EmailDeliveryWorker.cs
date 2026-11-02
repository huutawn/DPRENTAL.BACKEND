using System.Text.Json;
using Dprental.NotificationService.Application.Clients;
using Dprental.NotificationService.Application.Templates;
using Dprental.NotificationService.Domain.IRepositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.NotificationService.Application.Email.Workers;

public sealed class EmailDeliveryWorker : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private const int BatchSize = 20;

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<EmailDeliveryWorker> _logger;
    private readonly string _workerId;

    public EmailDeliveryWorker(IServiceProvider serviceProvider, ILogger<EmailDeliveryWorker> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workerId = $"worker_{Guid.NewGuid():N}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EmailDeliveryWorker ({WorkerId}) đã khởi động.", _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var deliveryRepo = scope.ServiceProvider.GetRequiredService<IDeliveryJobRepository>();
                var preferenceRepo = scope.ServiceProvider.GetRequiredService<IPreferenceRepository>();
                var authContactClient = scope.ServiceProvider.GetRequiredService<IAuthContactClient>();
                var templateRenderer = scope.ServiceProvider.GetRequiredService<INotificationTemplateRenderer>();
                var emailSender = scope.ServiceProvider.GetRequiredService<IEmailSender>();

                var claimedJobs = await deliveryRepo.ClaimPendingJobsAsync(_workerId, LeaseDuration, BatchSize, stoppingToken);
                if (claimedJobs.Count == 0)
                {
                    await Task.Delay(3000, stoppingToken);
                    continue;
                }

                _logger.LogInformation("EmailDeliveryWorker đã claim {Count} jobs.", claimedJobs.Count);

                // Batch resolve contacts from AuthService
                var recipientIds = claimedJobs.Select(j => j.RecipientUserId).Distinct().ToList();
                var contactsResult = await authContactClient.GetUserContactsBatchAsync(recipientIds, stoppingToken);
                var contactsMap = contactsResult.IsSuccess
                    ? contactsResult.Value.ToDictionary(c => c.UserId)
                    : new Dictionary<Guid, AuthContactDto>();

                foreach (var job in claimedJobs)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    if (job.LeaseToken == null) continue;

                    var notification = job.Notification;
                    if (notification == null)
                    {
                        await deliveryRepo.UpdateJobStatusAsync(job.Id, job.LeaseToken, "Failed", errorCode: "MISSING_NOTIFICATION", cancellationToken: stoppingToken);
                        continue;
                    }

                    // 1. Kiểm tra trạng thái tài khoản và email xác thực từ AuthService
                    if (!contactsMap.TryGetValue(job.RecipientUserId, out var contact) || !contact.IsActive || !contact.IsEmailVerified)
                    {
                        _logger.LogWarning("Bỏ qua gửi email do tài khoản không hoạt động hoặc email chưa xác thực: UserId={UserId}, JobId={JobId}", job.RecipientUserId, job.Id);
                        await deliveryRepo.UpdateJobStatusAsync(job.Id, job.LeaseToken, "Suppressed", errorCode: "INACTIVE_OR_UNVERIFIED_EMAIL", cancellationToken: stoppingToken);
                        continue;
                    }

                    // 2. Kiểm tra tùy chọn người dùng (User Preferences)
                    var isEmailAllowed = await preferenceRepo.IsEmailEnabledAsync(job.RecipientUserId, notification.Category, stoppingToken);
                    if (!isEmailAllowed)
                    {
                        _logger.LogInformation("Bỏ qua gửi email do người dùng đã tắt preference: UserId={UserId}, Category={Category}, JobId={JobId}", job.RecipientUserId, notification.Category, job.Id);
                        await deliveryRepo.UpdateJobStatusAsync(job.Id, job.LeaseToken, "Suppressed", errorCode: "PREFERENCE_DISABLED", cancellationToken: stoppingToken);
                        continue;
                    }

                    // 3. Render email template
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

                    var renderedResult = templateRenderer.Render(notification.NotificationType, renderData);
                    if (renderedResult.IsFailure || string.IsNullOrWhiteSpace(renderedResult.Value.EmailSubject) || string.IsNullOrWhiteSpace(renderedResult.Value.EmailBody))
                    {
                        await deliveryRepo.UpdateJobStatusAsync(job.Id, job.LeaseToken, "Suppressed", errorCode: "NO_EMAIL_TEMPLATE", cancellationToken: stoppingToken);
                        continue;
                    }

                    // 4. Ghi nhận thời điểm bắt đầu dispatch bền vững trước khi gọi SMTP
                    var now = DateTimeOffset.UtcNow;
                    await deliveryRepo.RecordDispatchStartAsync(job.Id, job.LeaseToken, now, stoppingToken);

                    // 5. Gửi Email qua SMTP adapter
                    var sendResult = await emailSender.SendEmailAsync(
                        contact.Email,
                        renderedResult.Value.EmailSubject,
                        renderedResult.Value.EmailBody,
                        stoppingToken);

                    // 6. Xử lý kết quả theo chính sách phân loại lỗi và lease fencing
                    switch (sendResult.Outcome)
                    {
                        case EmailSendOutcome.Success:
                            await deliveryRepo.UpdateJobStatusAsync(
                                job.Id,
                                job.LeaseToken,
                                "Succeeded",
                                providerMessageId: sendResult.ProviderMessageId,
                                cancellationToken: stoppingToken);
                            break;

                        case EmailSendOutcome.PreDataError:
                            if (job.AttemptCount < job.MaxAttempts)
                            {
                                var nextAttempt = now.AddMinutes(Math.Pow(2, job.AttemptCount));
                                await deliveryRepo.UpdateJobStatusAsync(
                                    job.Id,
                                    job.LeaseToken,
                                    "RetryScheduled",
                                    errorCode: sendResult.ErrorCode,
                                    errorDetail: sendResult.ErrorDetail,
                                    nextAttemptAt: nextAttempt,
                                    cancellationToken: stoppingToken);
                            }
                            else
                            {
                                await deliveryRepo.UpdateJobStatusAsync(
                                    job.Id,
                                    job.LeaseToken,
                                    "Failed",
                                    errorCode: "MAX_RETRIES_EXCEEDED",
                                    errorDetail: sendResult.ErrorDetail,
                                    cancellationToken: stoppingToken);
                            }
                            break;

                        case EmailSendOutcome.PostDataError:
                            // BẤT BIẾN: Lỗi xảy ra sau DATA không được tự động blind retry!
                            _logger.LogWarning("JobId={JobId} gặp lỗi sau DATA phase. Chuyển trạng thái sang Uncertain để tránh gửi trùng lặp.", job.Id);
                            await deliveryRepo.UpdateJobStatusAsync(
                                job.Id,
                                job.LeaseToken,
                                "Uncertain",
                                errorCode: sendResult.ErrorCode,
                                errorDetail: sendResult.ErrorDetail,
                                cancellationToken: stoppingToken);
                            break;

                        case EmailSendOutcome.PermanentRejection:
                            await deliveryRepo.UpdateJobStatusAsync(
                                job.Id,
                                job.LeaseToken,
                                "Failed",
                                errorCode: sendResult.ErrorCode,
                                errorDetail: sendResult.ErrorDetail,
                                cancellationToken: stoppingToken);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xảy ra trong vòng lặp EmailDeliveryWorker. Sẽ thử lại sau 5 giây.");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }
}
