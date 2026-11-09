namespace Dprental.RentalService.Application.DTOs.Settlement;

/// <summary>Một mức hư hại trong bảng hiện hành để supplier chọn khi đề nghị khấu trừ.</summary>
public sealed record DamageLevelDto(int Level, string Name, string Description, int Percent);
