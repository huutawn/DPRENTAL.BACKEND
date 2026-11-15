using System.Text.Json;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.DTOs.Settlement;

/// <summary>Một mức hư hại đóng băng trong snapshot của RentalItem lúc checkout.</summary>
public sealed record DamageLevelSnapshot(int Level, string Name, int Percent)
{
    public static string Serialize(IEnumerable<DamageLevel> levels) =>
        JsonSerializer.Serialize(levels.Select(l => new DamageLevelSnapshot(l.Level, l.Name, l.Percent)));

    public static IReadOnlyList<DamageLevelSnapshot>? Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<List<DamageLevelSnapshot>>(json);
}
