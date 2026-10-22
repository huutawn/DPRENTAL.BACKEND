using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;

namespace Dprental.RentalService.Application.Security;

/// <summary>
/// Frozen business authorization rules for RentalService operations.
/// Enforces resource-level ownership, profile completion, and role boundaries.
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
    /// Evaluates read access to a RentalOrder.
    /// Allowed if caller is platform admin or the renter who placed the order; anyone else gets Order.NotFound.
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
    /// Evaluates whether the caller can perform renter-scoped actions on an order (e.g. acknowledge handover, dispute deduction, cancel).
    /// Anyone but the renter who placed the order gets Order.NotFound.
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
    /// Evaluates whether the caller can perform supplier-scoped actions on a RentalItem (accept, reject, handover, return, propose deduction).
    /// Anyone but the item's supplier gets RentalItem.NotFound.
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
    /// Evaluates whether the caller has authority to resolve a dispute.
    /// Invariant: Only platform or system admins can resolve disputes.
    /// </summary>
    public static Result AuthorizeDisputeResolution(IRentalCallerContext caller) =>
        RequireAdmin(caller, "Service token không được giải quyết khiếu nại.", RentalErrors.AdminOnlyResolution);

    /// <summary>
    /// Evaluates whether the caller has authority to access admin supplier statistics.
    /// Invariant: Only authenticated human administrators can view supplier statistics.
    /// </summary>
    public static Result AuthorizeAdminStatisticsRead(IRentalCallerContext caller) =>
        RequireAdmin(
            caller,
            "Service token không được truy cập số liệu thống kê quản trị.",
            Error.Forbidden(ErrorCodes.Forbidden, "Chỉ quản trị viên mới có quyền xem thống kê của supplier."));

    /// <summary>
    /// Evaluates whether the caller may search and read any rental order, including internal references.
    /// Invariant: Only authenticated human administrators; service tokens are refused.
    /// </summary>
    public static Result AuthorizeAdminOrderRead(IRentalCallerContext caller) =>
        RequireAdmin(
            caller,
            "Service token không được tra cứu đơn thuê.",
            Error.Forbidden(ErrorCodes.Forbidden, "Chỉ quản trị viên mới được tra cứu mọi đơn thuê."));

    /// <summary>
    /// Evaluates whether the caller has authority to access private evidence media for a given rental item/order.
    /// Invariants:
    /// - Caller must be platform admin, the order's renter, or the item's supplier.
    /// - Service tokens are not permitted to access private user evidence.
    /// - MediaId must be present in the item's recorded evidence collections (handover, returns, deductions, or dispute).
    /// A stranger gets the same Media.EvidenceNotFound as an unrelated media id, so evidence ids cannot be probed.
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
        if (!isParticipant || !item.ContainsEvidenceMedia(mediaId))
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
