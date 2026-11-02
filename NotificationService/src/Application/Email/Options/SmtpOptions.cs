namespace Dprental.NotificationService.Application.Email.Options;

public sealed class SmtpOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1025; // Default for MailHog / test sink
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool EnableSsl { get; set; }
    public string SenderEmail { get; set; } = "noreply@dprental.vn";
    public string SenderName { get; set; } = "DPRental";
}
