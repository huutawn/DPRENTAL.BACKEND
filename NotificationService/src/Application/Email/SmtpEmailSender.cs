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
        Guid deliveryJobId,
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
        Guid deliveryJobId,
        string recipientEmail,
        string subject,
        string bodyHtml,
        CancellationToken cancellationToken = default)
    {
        var sendStarted = false;
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

            var messageId = $"<notif_{deliveryJobId:N}@dprental.vn>";
            message.Headers.Set("Message-ID", messageId);

            sendStarted = true;
            await client.SendMailAsync(message, cancellationToken);

            return new EmailSendResult(
                Outcome: EmailSendOutcome.Success,
                ProviderMessageId: messageId,
                ErrorCode: null,
                ErrorDetail: null);
        }
        catch (SmtpFailedRecipientException ex)
        {
            _logger.LogWarning(ex, "SMTP từ chối địa chỉ người nhận {Email}: {StatusCode}", recipientEmail, ex.StatusCode);
            var is5xx = (int)ex.StatusCode >= 500 && (int)ex.StatusCode < 600;
            var is4xx = (int)ex.StatusCode >= 400 && (int)ex.StatusCode < 500;
            if (is5xx)
            {
                return new EmailSendResult(
                    Outcome: EmailSendOutcome.PermanentRejection,
                    ProviderMessageId: null,
                    ErrorCode: "RECIPIENT_REJECTED",
                    ErrorDetail: $"SMTP StatusCode: {ex.StatusCode}");
            }
            if (is4xx)
            {
                return new EmailSendResult(
                    Outcome: EmailSendOutcome.PreDataError,
                    ProviderMessageId: null,
                    ErrorCode: "TRANSIENT_SMTP_4XX",
                    ErrorDetail: $"SMTP StatusCode: {ex.StatusCode}");
            }
            return new EmailSendResult(
                Outcome: sendStarted ? EmailSendOutcome.PostDataError : EmailSendOutcome.PreDataError,
                ProviderMessageId: null,
                ErrorCode: sendStarted ? "AMBIGUOUS_SMTP_ERROR" : "TRANSIENT_SMTP_ERROR",
                ErrorDetail: ex.Message);
        }
        catch (SmtpException ex) when (ex.InnerException is SocketException or TimeoutException or IOException)
        {
            _logger.LogWarning(ex, "Lỗi kết nối Socket hoặc Timeout khi gửi email tới {Email}", recipientEmail);
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
            var is4xx = (int)ex.StatusCode >= 400 && (int)ex.StatusCode < 500;
            if (is5xx)
            {
                return new EmailSendResult(
                    Outcome: EmailSendOutcome.PermanentRejection,
                    ProviderMessageId: null,
                    ErrorCode: "PERMANENT_SMTP_FAILURE",
                    ErrorDetail: ex.Message);
            }
            if (is4xx)
            {
                return new EmailSendResult(
                    Outcome: EmailSendOutcome.PreDataError,
                    ProviderMessageId: null,
                    ErrorCode: "TRANSIENT_SMTP_4XX",
                    ErrorDetail: ex.Message);
            }
            return new EmailSendResult(
                Outcome: sendStarted ? EmailSendOutcome.PostDataError : EmailSendOutcome.PreDataError,
                ProviderMessageId: null,
                ErrorCode: sendStarted ? "AMBIGUOUS_SMTP_ERROR" : "TRANSIENT_SMTP_ERROR",
                ErrorDetail: ex.Message);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or IOException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Timeout hoặc ngắt kết nối khi gửi email tới {Email}", recipientEmail);
            return new EmailSendResult(
                Outcome: sendStarted ? EmailSendOutcome.PostDataError : EmailSendOutcome.PreDataError,
                ProviderMessageId: null,
                ErrorCode: sendStarted ? "SMTP_TIMEOUT_OR_DROPPED" : "TRANSIENT_CONNECTION_ERROR",
                ErrorDetail: ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi không xác định khi gửi email tới {Email}", recipientEmail);
            return new EmailSendResult(
                Outcome: sendStarted ? EmailSendOutcome.PostDataError : EmailSendOutcome.PreDataError,
                ProviderMessageId: null,
                ErrorCode: sendStarted ? "AMBIGUOUS_SEND_ERROR" : "UNKNOWN_SEND_ERROR",
                ErrorDetail: ex.Message);
        }
    }
}
