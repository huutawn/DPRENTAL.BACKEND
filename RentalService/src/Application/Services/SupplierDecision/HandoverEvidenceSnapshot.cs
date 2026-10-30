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

    // Serialize snapshot bằng chứng bàn giao vào journal để dùng khi Inventory xác nhận, không lấy lại nội dung request đã thay đổi.
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    // Đọc snapshot journal có thể thiếu/hỏng; chỉ trả đối tượng khi parse thành công để caller quyết định nhánh đối soát.
    public static HandoverEvidenceSnapshot? TryParse(string? json)
    {
        // Phân biệt dữ liệu thiếu với nội dung có thể sử dụng trước khi chuẩn hóa, parse hoặc gửi sang bước kế tiếp.
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        // Parse dữ liệu JSON có thể hỏng hoặc không còn đúng dạng; xử lý lỗi ở nhánh catch thay vì dùng dữ liệu chưa xác minh.
        try
        {
            // Đọc dữ liệu đã serialize; các nhánh kiểm tra/catch phía sau quyết định có thể tiếp tục hay cần báo lỗi.
            HandoverEvidenceSnapshot? snapshot = JsonSerializer.Deserialize<HandoverEvidenceSnapshot>(json, JsonOptions);
            return snapshot is { Units: not null } && snapshot.SubmittedById != Guid.Empty ? snapshot : null;
        }
        // Dữ liệu JSON không đọc được: trả lỗi hoặc đi nhánh fallback đã định nghĩa, không dùng một snapshot dựng từ dữ liệu hỏng.
        catch (JsonException)
        {
            return null;
        }
    }

    // Chuyển bằng chứng snapshot từng unit sang dữ liệu tạo biên bản bàn giao sau xác nhận Inventory.
    public IReadOnlyList<HandoverUnitData> ToUnitData() =>
        Units.Select(u => new HandoverUnitData(u.AssignmentId, u.Condition, u.Note, u.MediaIds ?? [])).ToList();
}

public sealed record HandoverUnitSnapshot(
    Guid AssignmentId,
    UnitCondition Condition,
    string? Note,
    IReadOnlyList<Guid>? MediaIds);
