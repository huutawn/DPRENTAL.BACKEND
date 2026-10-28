namespace Dprental.RentalService.Common.Enums;

/// <summary>
/// Loại khoản hoàn cho người thuê của một dòng thiết bị.
/// </summary>
public enum RentalRefundKind
{
    /// <summary>Dòng bị hủy, bị nhà cung cấp từ chối hoặc quá hạn sau khi đã thanh toán: hoàn toàn bộ tiền thuê và tiền cọc.</summary>
    FullRefund = 0,

    /// <summary>Dòng đã trả máy và chốt khấu trừ: hoàn phần cọc còn lại.</summary>
    DepositReturn = 1
}
