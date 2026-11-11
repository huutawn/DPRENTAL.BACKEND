using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Common.Enums;
namespace Dprental.RentalService.Application.DTOs.Orders;

/// <summary>
/// Một máy cụ thể Inventory đã gán cho dòng, chỉ trả cho nhà cung cấp sở hữu máy.
/// </summary>
/// <param name="AssignmentId">Mã gán máy, gửi vào API ghi nhận trả máy.</param>
/// <param name="AssetId">Máy cụ thể của nhà cung cấp, gửi vào assessments[].assetId khi đề xuất khấu trừ.</param>
/// <param name="Status">Tình trạng của máy trong dòng: đã gán, đã nhả, đã giao, đã trả.</param>
/// <param name="ReturnCondition">Tình trạng lúc trả, rỗng khi chưa trả.</param>
public sealed record SupplierUnitResponse(
    Guid AssignmentId,
    Guid AssetId,
    RentalUnitAssignmentStatus Status,
    UnitCondition? ReturnCondition)
{
    public UnitCondition? HandoverCondition { get; init; }
    public string? HandoverNote { get; init; }
    public IReadOnlyList<Guid> HandoverMediaIds { get; init; } = [];
    public string? ReturnNote { get; init; }
    public IReadOnlyList<Guid> ReturnMediaIds { get; init; } = [];
}

/// <summary>Bằng chứng người thuê gửi cho đúng một máy trong một yêu cầu trả.</summary>
public sealed record SupplierUnitReturnEvidenceResponse(
    Guid ReturnRequestId,
    DateTimeOffset ActualReturnAt,
    DateTimeOffset SubmittedAt,
    string? Note,
    IReadOnlyList<Guid> MediaIds,
    ReturnRequestUnitStatus Status);

/// <summary>Chi tiết một máy trong đơn con của nhà cung cấp, gồm ảnh và tình trạng ở từng bước.</summary>
public sealed record SupplierUnitDetailResponse(
    Guid RentalItemId,
    Guid EquipmentListingId,
    string Name,
    RentalItemStatus ItemStatus,
    OrderListingSnapshotResponse Listing,
    SupplierUnitResponse Unit,
    IReadOnlyList<SupplierUnitReturnEvidenceResponse> RenterReturnEvidence);

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
/// <param name="ReturnRequests">Danh sách các yêu cầu trả thiết bị trên dòng của nhà cung cấp.</param>
/// <param name="PickupLocation">Địa chỉ kho nhận máy.</param>
/// <param name="ReviewStatus">Trạng thái đánh giá của dòng thiết bị.</param>
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
    IReadOnlyList<ReturnRequestResponse> ReturnRequests,
    string? PickupLocation,
    ReviewStatusDto ReviewStatus)
{
    /// <summary>Ảnh đại diện của listing tại thời điểm đặt hàng; dùng làm ảnh thiết bị ban đầu.</summary>
    public Guid? ThumbnailMediaId { get; init; }

    public SupplierOrderItemResponse(
        string name,
        int quantity,
        DateOnly startDate,
        DateOnly endDate,
        Guid rentalItemId,
        Guid equipmentListingId,
        RentalItemStatus status,
        int rentalDays,
        decimal dailyRate,
        decimal depositPerUnit,
        decimal rentalAmount,
        decimal depositAmount,
        DateTimeOffset? supplierConfirmationDeadline,
        DateTimeOffset? supplierDecisionAt,
        OrderHandoverResponse? handover,
        IReadOnlyList<OrderReturnResponse> returns,
        OrderSettlementResponse? settlement,
        IReadOnlyList<SupplierUnitResponse> units,
        IReadOnlyList<ReturnRequestResponse> returnRequests,
        string? pickupLocation = null)
        : this(name, quantity, startDate, endDate, rentalItemId, equipmentListingId, status,
               rentalDays, dailyRate, depositPerUnit, rentalAmount, depositAmount,
               supplierConfirmationDeadline, supplierDecisionAt, handover, returns, settlement,
               units, returnRequests, pickupLocation, ReviewStatusDto.Unknown)
    {
    }

    public SupplierOrderItemResponse(
        string name,
        int quantity,
        DateOnly startDate,
        DateOnly endDate,
        Guid rentalItemId,
        Guid equipmentListingId,
        RentalItemStatus status,
        int rentalDays,
        decimal dailyRate,
        decimal depositPerUnit,
        decimal rentalAmount,
        decimal depositAmount,
        DateTimeOffset? supplierConfirmationDeadline,
        DateTimeOffset? supplierDecisionAt,
        OrderHandoverResponse? handover,
        IReadOnlyList<OrderReturnResponse> returns,
        OrderSettlementResponse? settlement,
        IReadOnlyList<SupplierUnitResponse> units,
        string? pickupLocation = null)
        : this(name, quantity, startDate, endDate, rentalItemId, equipmentListingId, status,
               rentalDays, dailyRate, depositPerUnit, rentalAmount, depositAmount,
               supplierConfirmationDeadline, supplierDecisionAt, handover, returns, settlement,
               units, [], pickupLocation, ReviewStatusDto.Unknown)
    {
    }
}
