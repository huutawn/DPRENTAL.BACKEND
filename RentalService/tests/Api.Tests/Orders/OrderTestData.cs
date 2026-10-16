using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Api.Tests.Orders;

/// <summary>
/// Đơn hàng mẫu cho test xem đơn: thiết bị công trường của nhiều nhà cung cấp trong cùng một đơn.
/// </summary>
internal static class OrderTestData
{
    // 10:00 ngày 09/10/2026 giờ Việt Nam.
    public static readonly DateTimeOffset Now = new(2026, 10, 9, 3, 0, 0, TimeSpan.Zero);

    // Thuê ngày 12, 13, 14/10: đơn lưu ngày trả máy 15/10 (không tính).
    public static readonly DateOnly Start = new(2026, 10, 12);
    public static readonly DateOnly ReturnDate = new(2026, 10, 15);

    public const string RefundAccount = "VCB|0123456789|NGUYEN VAN A";

    public static InitialRentalItemDto Line(
        Guid supplierId,
        string title,
        string equipmentType,
        string brand,
        int quantity,
        decimal dailyRate,
        decimal depositPerUnit,
        DateOnly? start = null) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        supplierId,
        quantity,
        start ?? Start,
        ReturnDate,
        title,
        null,
        "Kho Thủ Đức, TP.HCM",
        dailyRate,
        depositPerUnit,
        $"grp-{Guid.NewGuid():N}",
        Enumerable.Range(0, quantity)
            .Select(_ => new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()))
            .ToList(),
        new RentalItemListingSnapshotDto(
            Guid.NewGuid(),
            equipmentType,
            Guid.NewGuid(),
            brand,
            title,
            1,
            """{"cong_suat_kw":55}""",
            Guid.NewGuid()));

    public static RentalOrder Order(Guid renterId, string orderCode, DateTimeOffset createdAt, params InitialRentalItemDto[] lines) =>
        RentalOrder.Create(
            Guid.NewGuid(),
            orderCode,
            renterId,
            "VND",
            "Delivery",
            "Nguyễn Văn An",
            "0901234567",
            "12 Võ Văn Ngân, Thủ Đức, TP.HCM",
            "Giao trước 7 giờ sáng",
            RefundAccount,
            "v1.0",
            "terms/v1.0.pdf",
            "hash123456",
            $"op-{Guid.NewGuid():N}",
            createdAt,
            lines).Value;

    public static RentalOrder Paid(RentalOrder order, DateTimeOffset paidAt)
    {
        Common.Result paid = order.MarkAsPaid($"INV-{order.OrderCode}", $"PAY-{order.OrderCode}", paidAt, paidAt);
        if (paid.IsFailure)
        {
            throw new InvalidOperationException(paid.Error.Message);
        }

        return order;
    }

    public static RentalCallerContext User(Guid userId) => RentalCallerContext.ForUser(userId);

    public static RentalCallerContext Service() => RentalCallerContext.ForService("inventory");

    public static RentalCallerContext Anonymous() => RentalCallerContext.Anonymous;
}
