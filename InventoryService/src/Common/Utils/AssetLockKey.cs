using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Dprental.InventoryService.Common.Utils;

/// <summary>
/// Tiện ích sinh khóa khóa độc quyền (Advisory Lock Key) kiểu bigint từ Asset ID.
/// Sử dụng thuật toán băm mật mã học ổn định SHA-256 thay vì hàm .NET GetHashCode().
/// </summary>
public static class AssetLockKey
{
    /// <summary>
    /// Chuyển đổi định danh Guid của tài sản thành số nguyên 64-bit (bigint trong PostgreSQL) có tính xác định tuyệt đối.
    /// Khóa này được sử dụng trực tiếp trong hàm pg_advisory_xact_lock(bigint).
    /// </summary>
    /// <param name="assetId">Định danh duy nhất của tài sản.</param>
    /// <returns>Số nguyên 64-bit biểu diễn khóa khóa độc quyền của tài sản.</returns>
    public static long FromAssetId(Guid assetId)
    {
        byte[] bytes = assetId.ToByteArray();
        byte[] hash = SHA256.HashData(bytes);
        return BinaryPrimitives.ReadInt64LittleEndian(hash);
    }
}
