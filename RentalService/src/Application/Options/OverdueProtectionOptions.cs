namespace Dprental.RentalService.Application.Options;

public sealed class OverdueProtectionOptions
{
    public const string SectionName = "Rental:OverdueProtection";

    public bool Enabled { get; set; } = true;
    public int ScanIntervalSeconds { get; set; } = 60;
    public int BatchSize { get; set; } = 50;
}
