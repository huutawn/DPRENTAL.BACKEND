using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Dprental.InventoryService.Common.Utils;

/// <summary>
/// Tiện ích sinh khóa khóa độc quyền (Advisory Lock Key) kiểu bigint từ Equipment ID.
/// Sử dụng thuật toán băm mật mã học ổn định SHA-256 thay vì hàm .NET GetHashCode() vốn không ổn định qua các lần chạy tiến trình.
/// </summary>
public static class EquipmentLockKey
{
    /// <summary>
    /// Chuyển đổi định danh Guid của thiết bị thành số nguyên 64-bit (bigint trong PostgreSQL) có tính xác định tuyệt đối.
    /// Khóa này được sử dụng trực tiếp trong hàm pg_advisory_xact_lock(bigint).
    /// </summary>
    /// <param name="equipmentId">Định danh duy nhất của thiết bị.</param>
    /// <returns>Số nguyên 64-bit biểu diễn khóa khóa độc quyền của thiết bị.</returns>
    public static long FromEquipmentId(Guid equipmentId)
    {
        byte[] bytes = equipmentId.ToByteArray();
        byte[] hash = SHA256.HashData(bytes);
        return BinaryPrimitives.ReadInt64LittleEndian(hash);
    }
}
