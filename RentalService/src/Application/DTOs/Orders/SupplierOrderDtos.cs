using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Application.DTOs.Orders;

/// <summary>
/// Một máy cụ thể Inventory đã gán cho dòng, chỉ trả cho nhà cung cấp sở hữu máy.
/// </summary>
/// <param name="AssignmentId">Mã gán máy, gửi vào API ghi nhận trả máy.</param>
/// <param name="AssetId">Máy cụ thể của nhà cung cấp, gửi vào affectedAssetIds khi đề xuất khấu trừ.</param>
/// <param name="Status">Tình trạng của máy trong dòng: đã gán, đã nhả, đã giao, đã trả.</param>
/// <param name="ReturnCondition">Tình trạng lúc trả, rỗng khi chưa trả.</param>
public sealed record SupplierUnitResponse(
    Guid AssignmentId,
    Guid AssetId,
    RentalUnitAssignmentStatus Status,
    UnitCondition? ReturnCondition);

/// <summary>
/// Một dòng thiết bị trong đơn con của nhà cung cấp. Giá là bản đã chụp lúc đặt hàng.
/// Không có tình trạng hoàn tiền hay tài khoản ngân hàng của người thuê.
/// </summary>
/// <param name="Name">Tên thiết bị lúc đặt hàng.</param>
/// <param name="Quantity">Số máy thuê.</param>
/// <param name="StartDate">Ngày đầu tiên dùng máy.</param>
/// <param name="EndDate">Ngày cuối cùng dùng máy (có tính), giống cách giỏ hàng hiển thị.</param>
/// <param name="RentalItemId">Mã định danh dòng, dùng cho các API bàn giao, trả máy và khấu trừ.</param>
/// <param name="EquipmentListingId">Thiết bị đã đặt.</param>
/// <param name="Status">Trạng thái của dòng.</param>
/// <param name="RentalDays">Số ngày thuê.</param>
/// <param name="DailyRate">Giá thuê một máy một ngày.</param>
/// <param name="DepositPerUnit">Tiền cọc một máy.</param>
/// <param name="RentalAmount">Tiền thuê = giá ngày x số máy x số ngày.</param>
/// <param name="DepositAmount">Tiền cọc = cọc một máy x số máy.</param>
/// <param name="SupplierConfirmationDeadline">Hạn nhà cung cấp xác nhận.</param>
/// <param name="SupplierDecisionAt">Thời điểm nhà cung cấp nhận hoặc từ chối.</param>
/// <param name="Handover">Biên bản bàn giao, rỗng khi chưa giao.</param>
/// <param name="Returns">Các lần trả máy, cũ nhất trước.</param>
/// <param name="Settlement">Quyết toán tiền cọc, rỗng khi chưa trả đủ máy.</param>
/// <param name="Units">Các máy cụ thể đã gán cho dòng.</param>
/// <param name="PickupLocation">Địa chỉ kho nhận máy; rỗng cho tới khi nhà cung cấp xác nhận dòng.</param>
public sealed record SupplierOrderItemResponse(
    string Name,
    int Quantity,
    DateOnly StartDate,
    DateOnly EndDate,
    Guid RentalItemId,
    Guid EquipmentListingId,
    RentalItemStatus Status,
    int RentalDays,
    decimal DailyRate,
    decimal DepositPerUnit,
    decimal RentalAmount,
    decimal DepositAmount,
    DateTimeOffset? SupplierConfirmationDeadline,
    DateTimeOffset? SupplierDecisionAt,
    OrderHandoverResponse? Handover,
    IReadOnlyList<OrderReturnResponse> Returns,
    OrderSettlementResponse? Settlement,
    IReadOnlyList<SupplierUnitResponse> Units,
    string? PickupLocation);
