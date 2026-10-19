namespace Dprental.RentalService.Application.Options;

public sealed class HandoverOverdueOptions
{
    public const string SectionName = "Rental:HandoverOverdue";

    public bool Enabled { get; set; } = true;
    public int ScanIntervalSeconds { get; set; } = 60;
    public int BatchSize { get; set; } = 50;
}
