namespace Dprental.RentalService.Application.Options;

public sealed class RentalReminderOptions
{
    public const string SectionName = "Rental:Reminder";

    public bool Enabled { get; set; } = true;
    public int BatchSize { get; set; } = 100;
    public int ScanIntervalSeconds { get; set; } = 60;
    public int LeaseDurationSeconds { get; set; } = 60;
    public int BaseRetryDelaySeconds { get; set; } = 30;
    public int MaxRetryDelaySeconds { get; set; } = 3600;
    public int MinClaimFailureBackoffSeconds { get; set; } = 5;
    public int MaxClaimFailureBackoffSeconds { get; set; } = 60;
}
