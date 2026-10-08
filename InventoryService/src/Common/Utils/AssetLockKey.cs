using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Dprental.InventoryService.Common.Utils;

/// <summary>
/// Tiện ích sinh khóa khóa độc quyền (Advisory Lock Key) kiểu bigint từ Group, Listing và Asset ID.
/// Sử dụng tiền tố namespace và thuật toán băm mật mã học ổn định SHA-256 để phân tách không gian khóa.
/// </summary>
public static class AssetLockKey
{
    /// <summary>
    /// Chuyển đổi định danh Guid của tài sản thành số nguyên 64-bit trong namespace "asset:".
    /// </summary>
    public static long FromAssetId(Guid assetId)
    {
        byte[] input = [.. "asset:"u8, .. assetId.ToByteArray()];
        byte[] hash = SHA256.HashData(input);
        return BinaryPrimitives.ReadInt64LittleEndian(hash);
    }

    /// <summary>
    /// Chuyển đổi định danh Guid của EquipmentListing thành số nguyên 64-bit trong namespace "listing:".
    /// </summary>
    public static long FromListingId(Guid listingId)
    {
        byte[] input = [.. "listing:"u8, .. listingId.ToByteArray()];
        byte[] hash = SHA256.HashData(input);
        return BinaryPrimitives.ReadInt64LittleEndian(hash);
    }

    /// <summary>
    /// Chuyển đổi bộ khóa (sourceSystem, groupReference) của request group thành số nguyên 64-bit trong namespace "group:".
    /// </summary>
    public static long FromGroup(string sourceSystem, string groupReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupReference);

        byte[] input = System.Text.Encoding.UTF8.GetBytes($"group:{sourceSystem.Trim()}:{groupReference.Trim()}");
        byte[] hash = SHA256.HashData(input);
        return BinaryPrimitives.ReadInt64LittleEndian(hash);
    }
}
