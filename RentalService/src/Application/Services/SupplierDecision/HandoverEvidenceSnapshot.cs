using System.Text.Json;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.Services.SupplierDecision;

/// <summary>
/// Bằng chứng bàn giao supplier đã gửi, giữ trong payload của receipt cho tới khi Inventory xác nhận.
/// Chỉ khi đó mới tạo bản ghi bàn giao thật, nên Inventory từ chối thì không còn gì cần xóa và supplier gửi lại được.
/// </summary>
public sealed record HandoverEvidenceSnapshot(
    Guid SubmittedById,
    DateTimeOffset SubmittedAt,
    DateTimeOffset ActualHandoverAt,
    string? Note,
    IReadOnlyList<HandoverUnitSnapshot> Units)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static HandoverEvidenceSnapshot? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            HandoverEvidenceSnapshot? snapshot = JsonSerializer.Deserialize<HandoverEvidenceSnapshot>(json, JsonOptions);
            return snapshot is { Units: not null } && snapshot.SubmittedById != Guid.Empty ? snapshot : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public IReadOnlyList<HandoverUnitData> ToUnitData() =>
        Units.Select(u => new HandoverUnitData(u.AssignmentId, u.Condition, u.Note, u.MediaIds ?? [])).ToList();
}

public sealed record HandoverUnitSnapshot(
    Guid AssignmentId,
    UnitCondition Condition,
    string? Note,
    IReadOnlyList<Guid>? MediaIds);
