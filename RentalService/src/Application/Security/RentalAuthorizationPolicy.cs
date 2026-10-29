using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;

namespace Dprental.RentalService.Application.Security;

/// <summary>
/// Các quy tắc phân quyền nghiệp vụ cho RentalService.
/// Kiểm tra quyền sở hữu tài nguyên, điều kiện hồ sơ và giới hạn theo vai trò.
/// </summary>
/// <remarks>
/// Đây là nơi duy nhất quyết định ai được làm gì: controller và service không tự so sánh RenterId/SupplierId.
/// Quy tắc IDOR: người không sở hữu tài nguyên nhận đúng lỗi 404 như khi tài nguyên không tồn tại, nên không dò
/// được id nào có thật. Lỗi 403 chỉ dành cho sai vai trò (ví dụ không phải Admin), không phụ thuộc vào id.
/// </remarks>
public static class RentalAuthorizationPolicy
{
    public static Result<Guid> AuthorizeRenterCaller(IRentalCallerContext? caller)
    {
        if (caller is null || !caller.IsAuthenticated || caller.IsService ||
            !caller.UserId.HasValue || caller.UserId.Value == Guid.Empty)
        {
            return Error.Unauthorized(ErrorCodes.AuthRequired, "Yêu cầu đăng nhập tài khoản cá nhân của người thuê.");
        }

        return caller.UserId.Value;
    }

    public static class ErrorCodes
    {
        public const string Unauthorized = "Auth.Unauthorized";
        public const string Forbidden = "Auth.Forbidden";
        public const string ServiceTokenNotAllowed = "Auth.ServiceTokenNotAllowed";

        /// <summary>Mã 401 mà các API thao tác của supplier đã dùng từ trước.</summary>
        public const string AuthRequired = "AUTH_REQUIRED";

        public const string EvidenceNotFound = "Media.EvidenceNotFound";
    }


    /// <summary>
    /// Cho phép Admin hoặc renter đặt đơn đọc RentalOrder; người khác nhận Order.NotFound để chống dò ID.
    /// </summary>
    public static Result AuthorizeOrderRead(IRentalCallerContext caller, RentalOrder order)
    {
        Result<Guid> user = RequireUser(caller, "Service token không được truy cập đơn thuê của người dùng.");
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (caller.IsAdmin() || user.Value == order.RenterId)
        {
            return Result.Success();
        }

        return RentalErrors.OrderNotFound;
    }

    /// <summary>
    /// Kiểm tra thao tác của renter trên đơn, như xác nhận nhận máy, tranh chấp khấu trừ hoặc hủy.
    /// Chỉ renter đặt đơn được thực hiện; người khác nhận Order.NotFound.
    /// </summary>
    public static Result AuthorizeRenterAction(IRentalCallerContext caller, RentalOrder order)
    {
        Result<Guid> user = RequireUser(caller, "Service token không được thực hiện thao tác người thuê.");
        if (user.IsFailure)
        {
            return user.Error;
        }

        return user.Value == order.RenterId ? Result.Success() : RentalErrors.OrderNotFound;
    }

    /// <summary>
    /// Kiểm tra quyền supplier nhận/từ chối, bàn giao, nhận trả hoặc đề xuất khấu trừ trên item.
    /// Người không sở hữu item nhận RentalItem.NotFound.
    /// </summary>
    public static Result AuthorizeSupplierAction(IRentalCallerContext caller, RentalItem item)
    {
        Result<Guid> user = RequireUser(caller, "Service token không được đóng vai supplier.");
        if (user.IsFailure)
        {
            return user.Error;
        }

        return AuthorizeSupplierItem(user.Value, item);
    }

    /// <summary>
    /// Danh tính của supplier gọi API thao tác: phải là tài khoản cá nhân đã đăng nhập, không phải service token.
    /// Giữ mã 401 AUTH_REQUIRED mà các API thao tác của supplier đã dùng.
    /// </summary>
    public static Result<Guid> AuthorizeSupplierCaller(IRentalCallerContext? caller)
    {
        if (caller is null || !caller.IsAuthenticated || caller.IsService || !caller.UserId.HasValue || caller.UserId.Value == Guid.Empty)
        {
            return Error.Unauthorized(ErrorCodes.AuthRequired, "Yêu cầu đăng nhập tài khoản cá nhân của supplier.");
        }

        return caller.UserId.Value;
    }

    /// <summary>
    /// Khoản thuê phải thuộc supplier. Khoản không tồn tại và khoản của supplier khác trả cùng một lỗi
    /// RentalItem.NotFound, nên không dò được id.
    /// </summary>
    public static Result<RentalItem> AuthorizeSupplierItem(Guid supplierId, RentalItem? item)
    {
        if (item is null || supplierId == Guid.Empty || item.SupplierId != supplierId)
        {
            return RentalErrors.ItemNotFound;
        }

        return item;
    }

    /// <summary>
    /// Đơn con phải thuộc supplier. Đơn con không tồn tại và đơn con của supplier khác trả cùng một lỗi
    /// SUB_ORDER_NOT_FOUND.
    /// </summary>
    public static Result<RentalSubOrder> AuthorizeSupplierSubOrder(Guid supplierId, RentalSubOrder? subOrder)
    {
        if (subOrder is null || supplierId == Guid.Empty || subOrder.SupplierId != supplierId)
        {
            return RentalErrors.SubOrderNotFound;
        }

        return subOrder;
    }

    /// <summary>
    /// Xem tác vụ của một đơn: Admin, người thuê của đơn hoặc supplier có dòng trong đơn. Đơn không tồn tại và đơn
    /// của người khác trả cùng một lỗi Order.NotFound.
    /// </summary>
    public static Result AuthorizeOperationRead(IRentalCallerContext caller, RentalOrder? order)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized(ErrorCodes.AuthRequired, "Yêu cầu đăng nhập.");
        }

        if (order is null)
        {
            return RentalErrors.OrderNotFound;
        }

        if (caller.IsAdmin())
        {
            return Result.Success();
        }

        Guid? userId = caller.UserId;
        bool isParticipant = userId.HasValue &&
            (order.RenterId == userId.Value || order.Items.Any(i => i.SupplierId == userId.Value));
        return isParticipant ? Result.Success() : RentalErrors.OrderNotFound;
    }

    /// <summary>
    /// Chỉ Admin hệ thống/nền tảng được chốt tranh chấp.
    /// </summary>
    public static Result AuthorizeDisputeResolution(IRentalCallerContext caller) =>
        RequireAdmin(caller, "Service token không được giải quyết khiếu nại.", RentalErrors.AdminOnlyResolution);

    public static Result AuthorizeAdminReturnResolution(IRentalCallerContext caller) =>
        RequireAdmin(caller, "Service token không được giải quyết yêu cầu trả máy.", RentalErrors.AdminOnlyReturnResolution);

    public static Result AuthorizeAdminReturnQuery(IRentalCallerContext caller) =>
        RequireAdmin(
            caller,
            "Service token không được tra cứu yêu cầu trả máy.",
            Error.Forbidden(ErrorCodes.Forbidden, "Chỉ quản trị viên mới được tra cứu yêu cầu trả máy."));
    /// <summary>
    /// Chỉ Admin cá nhân đã đăng nhập được đọc thống kê supplier; từ chối token service.
    /// </summary>
    public static Result AuthorizeAdminStatisticsRead(IRentalCallerContext caller) =>
        RequireAdmin(
            caller,
            "Service token không được truy cập số liệu thống kê quản trị.",
            Error.Forbidden(ErrorCodes.Forbidden, "Chỉ quản trị viên mới có quyền xem thống kê của supplier."));

    /// <summary>
    /// Chỉ Admin cá nhân đã đăng nhập được tìm/đọc mọi đơn và tham chiếu nội bộ; từ chối token service.
    /// </summary>
    public static Result AuthorizeAdminOrderRead(IRentalCallerContext caller) =>
        RequireAdmin(
            caller,
            "Service token không được tra cứu đơn thuê.",
            Error.Forbidden(ErrorCodes.Forbidden, "Chỉ quản trị viên mới được tra cứu mọi đơn thuê."));

    /// <summary>
    /// Chỉ Admin cá nhân đã đăng nhập được xác nhận chuyển khoản hoàn tiền; từ chối token service.
    /// </summary>
    public static Result AuthorizeAdminRefundConfirm(IRentalCallerContext caller) =>
        RequireAdmin(
            caller,
            "Service token không được xác nhận hoàn tiền.",
            Error.Forbidden(ErrorCodes.Forbidden, "Chỉ quản trị viên mới được xác nhận hoàn tiền."));

    /// <summary>
    /// Kiểm tra quyền đọc bằng chứng riêng tư của order/item.
    /// Caller phải là Admin, renter của order hoặc supplier của item; không cho token service truy cập.
    /// MediaId phải thuộc bằng chứng đã ghi của bàn giao, trả máy, khấu trừ hoặc tranh chấp.
    /// Người ngoài và MediaId không liên quan cùng nhận Media.EvidenceNotFound để chống dò ID.
    /// </summary>
    public static Result AuthorizeEvidenceRead(
        IRentalCallerContext caller,
        RentalOrder order,
        RentalItem item,
        Guid mediaId)
    {
        Result<Guid> user = RequireUser(caller, "Service token không được truy cập bằng chứng riêng tư.");
        if (user.IsFailure)
        {
            return user.Error;
        }

        bool isParticipant = user.Value == order.RenterId || user.Value == item.SupplierId || caller.IsAdmin();
        bool containsMedia = item.ContainsEvidenceMedia(mediaId) ||
            item.ReturnRequests.SelectMany(r => r.Units).Any(u => u.MediaIds.Contains(mediaId)) ||
            item.ReturnRequests.SelectMany(r => r.Issues).Any(i => i.MediaIds.Contains(mediaId)) ||
            item.ReturnRequests.SelectMany(r => r.Resolutions).Any(res => res.ResolutionMediaIds.Contains(mediaId));
        if (!isParticipant || !containsMedia)
        {
            return Error.NotFound(ErrorCodes.EvidenceNotFound, "MediaId không thuộc bằng chứng của khoản thuê này.");
        }
        return Result.Success();
    }

    /// <summary>Phải đăng nhập, không phải service token và có danh tính người dùng; trả mã người dùng.</summary>
    private static Result<Guid> RequireUser(IRentalCallerContext caller, string serviceTokenMessage)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized(ErrorCodes.Unauthorized, "Yêu cầu đăng nhập.");
        }

        if (caller.IsService)
        {
            return Error.Forbidden(ErrorCodes.ServiceTokenNotAllowed, serviceTokenMessage);
        }

        if (!caller.UserId.HasValue)
        {
            return Error.Unauthorized(ErrorCodes.Unauthorized, "Yêu cầu danh tính người dùng hợp lệ.");
        }

        return caller.UserId.Value;
    }

    private static Result RequireAdmin(IRentalCallerContext caller, string serviceTokenMessage, Error notAdmin)
    {
        Result<Guid> user = RequireUser(caller, serviceTokenMessage);
        if (user.IsFailure)
        {
            return user.Error;
        }

        return caller.IsAdmin() ? Result.Success() : notAdmin;
    }
}
