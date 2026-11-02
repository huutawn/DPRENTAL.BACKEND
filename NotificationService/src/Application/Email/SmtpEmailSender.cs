using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using Dprental.NotificationService.Application.Email.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.NotificationService.Application.Email;

public enum EmailSendOutcome
{
    Success,
    PreDataError,
    PostDataError,
    PermanentRejection
}

public sealed record EmailSendResult(
    EmailSendOutcome Outcome,
    string? ProviderMessageId,
    string? ErrorCode,
    string? ErrorDetail);

public interface IEmailSender
{
    Task<EmailSendResult> SendEmailAsync(
        string recipientEmail,
        string subject,
        string bodyHtml,
        CancellationToken cancellationToken = default);
}

public sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<EmailSendResult> SendEmailAsync(
        string recipientEmail,
        string subject,
        string bodyHtml,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new SmtpClient(_options.Host, _options.Port)
            {
                EnableSsl = _options.EnableSsl,
                Timeout = 10000 // 10 seconds timeout
            };

            if (!string.IsNullOrWhiteSpace(_options.Username))
            {
                client.Credentials = new NetworkCredential(_options.Username, _options.Password);
            }

            using var message = new MailMessage
            {
                From = new MailAddress(_options.SenderEmail, _options.SenderName),
                Subject = subject,
                Body = bodyHtml,
                IsBodyHtml = true
            };
            message.To.Add(recipientEmail);

            await client.SendMailAsync(message, cancellationToken);

            var messageId = $"smtp_{Guid.NewGuid():N}";
            return new EmailSendResult(
                Outcome: EmailSendOutcome.Success,
                ProviderMessageId: messageId,
                ErrorCode: null,
                ErrorDetail: null);
        }
        catch (SmtpFailedRecipientException ex)
        {
            _logger.LogWarning(ex, "SMTP từ chối địa chỉ người nhận {Email}: {StatusCode}", recipientEmail, ex.StatusCode);
            return new EmailSendResult(
                Outcome: EmailSendOutcome.PermanentRejection,
                ProviderMessageId: null,
                ErrorCode: "RECIPIENT_REJECTED",
                ErrorDetail: $"SMTP StatusCode: {ex.StatusCode}");
        }
        catch (SmtpException ex) when (ex.InnerException is SocketException or TimeoutException)
        {
            _logger.LogWarning(ex, "Lỗi kết nối Socket hoặc Timeout khi gửi email tới {Email}", recipientEmail);
            // Lỗi socket hoặc timeout có thể xảy ra sau DATA
            return new EmailSendResult(
                Outcome: EmailSendOutcome.PostDataError,
                ProviderMessageId: null,
                ErrorCode: "SMTP_TIMEOUT_OR_DROPPED",
                ErrorDetail: ex.Message);
        }
        catch (SmtpException ex)
        {
            _logger.LogWarning(ex, "Lỗi SMTP khi gửi email tới {Email}: {StatusCode}", recipientEmail, ex.StatusCode);
            var is5xx = (int)ex.StatusCode >= 500 && (int)ex.StatusCode < 600;
            return new EmailSendResult(
                Outcome: is5xx ? EmailSendOutcome.PermanentRejection : EmailSendOutcome.PreDataError,
                ProviderMessageId: null,
                ErrorCode: is5xx ? "PERMANENT_SMTP_FAILURE" : "TRANSIENT_SMTP_ERROR",
                ErrorDetail: ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi không xác định khi gửi email tới {Email}", recipientEmail);
            return new EmailSendResult(
                Outcome: EmailSendOutcome.PreDataError,
                ProviderMessageId: null,
                ErrorCode: "UNKNOWN_SEND_ERROR",
                ErrorDetail: ex.Message);
        }
    }
}
