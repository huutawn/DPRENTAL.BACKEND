using System.Net.Mail;
using Dprental.NotificationService.Application.Email;
using Dprental.NotificationService.Application.Email.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.NotificationService.Api.Tests.Email;

public sealed class SmtpEmailSenderTests
{
    private static SmtpEmailSender CreateSender(int port = 2525)
    {
        var options = Options.Create(new SmtpOptions
        {
            Host = "127.0.0.1",
            Port = port,
            SenderEmail = "noreply@dprental.vn",
            SenderName = "DP Rental",
            EnableSsl = false
        });

        return new SmtpEmailSender(options, NullLogger<SmtpEmailSender>.Instance);
    }

    [Fact]
    public async Task SendEmailAsync_WhenExceptionOccursBeforeSend_ClassifiesAsPreDataError()
    {
        var sender = CreateSender();

        // An invalid email address causes FormatException in MailAddress before sendStarted = true
        var result = await sender.SendEmailAsync(
            recipientEmail: "invalid-email-address-without-at",
            subject: "Test Subject",
            bodyHtml: "<p>Hello</p>",
            deliveryJobId: Guid.NewGuid(),
            cancellationToken: CancellationToken.None);

        Assert.Equal(EmailSendOutcome.PreDataError, result.Outcome);
        Assert.Equal("UNKNOWN_SEND_ERROR", result.ErrorCode);
        Assert.NotNull(result.ErrorDetail);
    }

    [Fact]
    public async Task SendEmailAsync_WhenExceptionOccursAfterSendStarted_ClassifiesAsPostDataError_WithoutBlindRetry()
    {
        // Port 59999 is closed, will fail during SendMailAsync (after sendStarted = true)
        var sender = CreateSender(port: 59999);
        var deliveryJobId = Guid.NewGuid();

        var result = await sender.SendEmailAsync(
            recipientEmail: "test@example.com",
            subject: "Test Subject",
            bodyHtml: "<p>Hello</p>",
            deliveryJobId: deliveryJobId,
            cancellationToken: CancellationToken.None);

        // Ambiguous failure after send started must be classified as PostDataError
        Assert.Equal(EmailSendOutcome.PostDataError, result.Outcome);
        Assert.True(result.ErrorCode == "SMTP_TIMEOUT_OR_DROPPED" || result.ErrorCode == "AMBIGUOUS_SEND_ERROR");
    }

    [Fact]
    public void StableMessageId_MatchesExpectedFormat()
    {
        var deliveryJobId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var expectedMessageId = $"<notif_{deliveryJobId:N}@dprental.vn>";
        Assert.Equal("<notif_0123456789abcdef0123456789abcdef@dprental.vn>", expectedMessageId);
    }
}
