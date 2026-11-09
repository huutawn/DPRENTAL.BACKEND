using System;
using System.Text.Json;

namespace Dprental.RentalService.Application.DTOs.ReviewIntegration;

/// <summary>
/// Quản lý mã hóa/giải mã keyset cursor cho API đối soát mục thuê.
/// Cố định cửa sổ 30 ngày (WindowStart) và vị trí trang (LastCompletedAt, LastItemId).
/// </summary>
public sealed record ReviewReconciliationCursor(
    DateTimeOffset WindowStart,
    DateTimeOffset? LastCompletedAt,
    Guid? LastItemId)
{
    public static string Encode(ReviewReconciliationCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        var dto = new CursorPayload(
            cursor.WindowStart.ToUniversalTime(),
            cursor.LastCompletedAt?.ToUniversalTime(),
            cursor.LastItemId);

        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(dto);
        return Convert.ToBase64String(jsonBytes);
    }

    public static bool TryDecode(string? rawCursor, out ReviewReconciliationCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(rawCursor))
        {
            return false;
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(rawCursor.Trim());
            var payload = JsonSerializer.Deserialize<CursorPayload>(bytes);
            if (payload == null || payload.WindowStart == default)
            {
                return false;
            }

            cursor = new ReviewReconciliationCursor(
                payload.WindowStart.ToUniversalTime(),
                payload.LastCompletedAt?.ToUniversalTime(),
                payload.LastItemId);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record CursorPayload(
        DateTimeOffset WindowStart,
        DateTimeOffset? LastCompletedAt,
        Guid? LastItemId);
}
