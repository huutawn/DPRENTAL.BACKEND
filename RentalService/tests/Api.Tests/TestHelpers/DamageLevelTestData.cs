using Dprental.RentalService.Application.DTOs.Settlement;

namespace Dprental.RentalService.Api.Tests.TestHelpers;

/// <summary>Snapshot bảng mức hư hại chuẩn (0/10/30/50/100%) cho item dựng tay trong test.</summary>
internal static class DamageLevelTestData
{
    public static string SnapshotJson { get; } = System.Text.Json.JsonSerializer.Serialize(new[]
    {
        new DamageLevelSnapshot(0, "Không hư", 0),
        new DamageLevelSnapshot(1, "Nhẹ", 10),
        new DamageLevelSnapshot(2, "Vừa", 30),
        new DamageLevelSnapshot(3, "Nặng", 50),
        new DamageLevelSnapshot(4, "Hỏng hoàn toàn", 100)
    });
}
