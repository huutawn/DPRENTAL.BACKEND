using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common.Enums;
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

    // Cùng dạng checkout ghi vào RefundDestinationRef.
    public const string RefundAccount =
        """{"bankCode":"VCB","accountNumber":"0123456789","accountHolderName":"NGUYỄN VĂN AN"}""";

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

    /// <summary>Đưa dòng sang đang thuê như sau khi nhà cung cấp giao đủ máy (luồng bàn giao chưa có API).</summary>
    public static HandoverRecord HandOver(RentalItem item, DateTimeOffset handedOverAt, string? note = null)
    {
        typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(item, RentalItemStatus.InRental);
        HandoverRecord handover = new(Guid.NewGuid(), item.Id, handedOverAt, note, item.SupplierId, handedOverAt, handedOverAt);
        item.SetHandoverRecord(handover);
        foreach (RentalUnitAssignment unit in item.UnitAssignments)
        {
            unit.RecordHandover(handover.Id, UnitCondition.Normal, null, []);
            handover.AddAssignment(unit);
        }

        return handover;
    }

    /// <summary>
    /// Nối dòng với đơn như EF làm khi load từ database, để quyết định của nhà cung cấp ghi được lịch sử trạng thái.
    /// </summary>
    public static void LinkItemsToOrder(RentalOrder order)
    {
        foreach (RentalItem item in order.Items)
        {
            typeof(RentalItem).GetProperty(nameof(RentalItem.RentalOrder))!.SetValue(item, order);
        }
    }

    /// <summary>Receipt RequestRefund mà luồng từ chối/hết hạn tạo khi yêu cầu PaymentService hoàn tiền dòng này.</summary>
    public static void RequestRefund(RentalOrder order, RentalItem item, DateTimeOffset requestedAt) =>
        order.AddOperationReceipt(new RentalOperationReceipt(
            Guid.NewGuid(),
            order.Id,
            $"refund-{item.Id:N}",
            "RequestRefund",
            "fingerprint",
            isSuccess: false,
            errorCode: null,
            errorMessage: null,
            createdAt: requestedAt,
            status: RentalOperationReceipt.StatusPending,
            targetItemId: item.Id));

    public static RentalCallerContext User(Guid userId) => RentalCallerContext.ForUser(userId);

    public static RentalCallerContext Service() => RentalCallerContext.ForService("inventory");

    public static RentalCallerContext Anonymous() => RentalCallerContext.Anonymous;
}
