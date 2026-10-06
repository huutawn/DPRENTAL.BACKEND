using System.IO;
using System.Text.Json;

namespace Dprental.InventoryService.Application.Security;

/// <summary>
/// Chính sách bảo vệ dữ liệu công khai: quản lý danh sách các thuộc tính nội bộ/nhạy cảm
/// không được phép xuất hiện trong JSON công khai hoặc được dùng làm bộ lọc tìm kiếm công khai.
/// </summary>
public static class PublicAssetDataPolicy
{
    private static readonly HashSet<string> ReservedPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Owner identity & Organization
        "ownerUserId", "owner_user_id", "ownerId", "owner_id",
        "organizationId", "organization_id",

        // Serial number & Display
        "serialNumber", "serial_number", "serial", "serialNo", "serial_no",
        "displayName", "display_name",

        // Suspension reason
        "suspensionReason", "suspension_reason",

        // Concurrency fields
        "version", "xmin",

        // Scheduling & Allocation
        "calendarBlocks", "calendar_blocks", "allocations", "holdExpiresAt", "hold_expires_at",
        "allocationId", "allocation_id",
        "externalReference", "external_reference", "sourceSystem", "source_system",

        // Renter / RentalOrder / Reservation
        "renterId", "renter_id",
        "renterUserId", "renter_user_id",
        "orderId", "order_id",
        "orderReference", "order_reference",
        "rentalOrderId", "rental_order_id",
        "rentalOrderReference", "rental_order_reference",
        "reservationId", "reservation_id",
        "reservationReference", "reservation_reference",

        // Audit metadata
        "createdAt", "created_at", "updatedAt", "updated_at",
        "createdBy", "created_by", "updatedBy", "updated_by",

        // Owner permissions
        "permissions", "permission",
        "inventoryAssetManage", "inventory_asset_manage", "inventory.asset.manage"
    };

    /// <summary>
    /// Kiểm tra xem tên thuộc tính có thuộc tập thuộc tính nội bộ bị cấm hay không.
    /// So khớp không phân biệt hoa thường và hỗ trợ cả camelCase lẫn snake_case.
    /// schemaVersion được phép công khai rõ ràng.
    /// </summary>
    public static bool IsReservedPropertyName(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        string trimmed = propertyName.Trim();
        if (string.Equals(trimmed, "schemaVersion", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "schema_version", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ReservedPropertyNames.Contains(trimmed);
    }

    /// <summary>
    /// Làm sạch JSON specification: loại bỏ mọi trường thuộc tập bảo lưu, kể cả trong các object lồng nhau.
    /// </summary>
    public static JsonElement SanitizeSpecValues(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object && element.ValueKind != JsonValueKind.Array)
        {
            return element.Clone();
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteSanitizedElement(element, writer);
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    private static void WriteSanitizedElement(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    if (IsReservedPropertyName(prop.Name))
                    {
                        continue;
                    }
                    writer.WritePropertyName(prop.Name);
                    WriteSanitizedElement(prop.Value, writer);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteSanitizedElement(item, writer);
                }
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
