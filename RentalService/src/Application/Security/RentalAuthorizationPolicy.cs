using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;

namespace Dprental.RentalService.Application.Security;

/// <summary>
/// Frozen business authorization rules for RentalService operations.
/// Enforces resource-level ownership, profile completion, and role boundaries.
/// </summary>
public static class RentalAuthorizationPolicy
{
    public static class ErrorCodes
    {
        public const string Unauthorized = "Auth.Unauthorized";
        public const string ProfileIncomplete = "Auth.ProfileIncomplete";
        public const string Forbidden = "Auth.Forbidden";
        public const string ServiceTokenNotAllowed = "Auth.ServiceTokenNotAllowed";
    }

    /// <summary>
    /// Evaluates whether the caller is eligible to perform checkout.
    /// Invariant: Must be authenticated, interactive user (not a service token), and have IsProfileCompleted == true in JWT claims.
    /// </summary>
    public static Result AuthorizeCheckout(IRentalCallerContext caller)
    {
        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized(ErrorCodes.Unauthorized, "Yêu cầu đăng nhập để thực hiện đặt thuê.");
        }

        if (caller.IsService)
        {
            return Error.Forbidden(ErrorCodes.ServiceTokenNotAllowed, "Service token không được thực hiện đặt đơn thuê của người dùng.");
        }

        if (!caller.UserId.HasValue)
        {
            return Error.Unauthorized(ErrorCodes.Unauthorized, "Yêu cầu danh tính người dùng.");
        }

        if (!caller.IsProfileCompleted)
        {
            return Error.Forbidden(ErrorCodes.ProfileIncomplete, "Hồ sơ cá nhân chưa hoàn thiện (yêu cầu hoàn tất hồ sơ trước khi thuê thiết bị).");
        }

        return Result.Success();
    }

    /// <summary>
    /// Evaluates read access to a RentalOrder.
    /// Allowed if caller is platform admin or the renter who placed the order.
    /// </summary>
    public static Result AuthorizeOrderRead(IRentalCallerContext caller, RentalOrder order)
    {
        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized(ErrorCodes.Unauthorized, "Yêu cầu đăng nhập.");
        }

        if (caller.IsService)
        {
            return Error.Forbidden(ErrorCodes.ServiceTokenNotAllowed, "Service token không được truy cập đơn thuê của người dùng.");
        }

        if (!caller.UserId.HasValue)
        {
            return Error.Unauthorized(ErrorCodes.Unauthorized, "Yêu cầu danh tính người dùng hợp lệ.");
        }

        if (caller.IsAdmin())
        {
            return Result.Success();
        }

        if (caller.UserId.Value == order.RenterId)
        {
            return Result.Success();
        }

        return Error.Forbidden(ErrorCodes.Forbidden, "Không có quyền truy cập đơn thuê này.");
    }

    /// <summary>
    /// Evaluates whether the caller can perform renter-scoped actions on an order (e.g. acknowledge handover, dispute deduction, cancel).
    /// </summary>
    public static Result AuthorizeRenterAction(IRentalCallerContext caller, RentalOrder order)
    {
        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized(ErrorCodes.Unauthorized, "Yêu cầu đăng nhập.");
        }

        if (caller.IsService)
        {
            return Error.Forbidden(ErrorCodes.ServiceTokenNotAllowed, "Service token không được thực hiện thao tác người thuê.");
        }

        if (!caller.UserId.HasValue)
        {
            return Error.Unauthorized(ErrorCodes.Unauthorized, "Yêu cầu danh tính người dùng.");
        }

        if (caller.UserId.Value != order.RenterId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Chỉ người thuê đặt đơn mới có quyền thực hiện thao tác này.");
        }

        return Result.Success();
    }

    /// <summary>
    /// Evaluates whether the caller can perform supplier-scoped actions on a RentalItem (accept, reject, handover, return, propose deduction).
    /// </summary>
    public static Result AuthorizeSupplierAction(IRentalCallerContext caller, RentalItem item)
    {
        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized(ErrorCodes.Unauthorized, "Yêu cầu đăng nhập.");
        }

        if (caller.IsService)
        {
            return Error.Forbidden(ErrorCodes.ServiceTokenNotAllowed, "Service token không được đóng vai supplier.");
        }

        if (!caller.UserId.HasValue)
        {
            return Error.Unauthorized(ErrorCodes.Unauthorized, "Yêu cầu danh tính người dùng.");
        }

        if (caller.UserId.Value != item.SupplierId)
        {
            return RentalErrors.SupplierMismatch;
        }

        return Result.Success();
    }

    /// <summary>
    /// Evaluates whether the caller has authority to resolve a dispute.
    /// Invariant: Only platform or system admins can resolve disputes.
    /// </summary>
    public static Result AuthorizeDisputeResolution(IRentalCallerContext caller)
    {
        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized(ErrorCodes.Unauthorized, "Yêu cầu đăng nhập.");
        }

        if (caller.IsService)
        {
            return Error.Forbidden(ErrorCodes.ServiceTokenNotAllowed, "Service token không được giải quyết khiếu nại.");
        }

        if (!caller.UserId.HasValue)
        {
            return Error.Unauthorized(ErrorCodes.Unauthorized, "Yêu cầu danh tính người dùng hợp lệ.");
        }

        if (!caller.IsAdmin())
        {
            return RentalErrors.AdminOnlyResolution;
        }

        return Result.Success();
    }

    /// <summary>
    /// Evaluates whether the caller has authority to access admin supplier statistics.
    /// Invariant: Only authenticated human administrators can view supplier statistics.
    /// </summary>
    public static Result AuthorizeAdminStatisticsRead(IRentalCallerContext caller)
    {
        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized(ErrorCodes.Unauthorized, "Yêu cầu đăng nhập.");
        }

        if (caller.IsService)
        {
            return Error.Forbidden(ErrorCodes.ServiceTokenNotAllowed, "Service token không được truy cập số liệu thống kê quản trị.");
        }

        if (!caller.UserId.HasValue)
        {
            return Error.Unauthorized(ErrorCodes.Unauthorized, "Yêu cầu danh tính người dùng hợp lệ.");
        }

        if (!caller.IsAdmin())
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Chỉ quản trị viên mới có quyền xem thống kê của supplier.");
        }

        return Result.Success();
    }

    /// <summary>
    /// Evaluates whether the caller may search and read any rental order, including internal references.
    /// Invariant: Only authenticated human administrators; service tokens are refused.
    /// </summary>
    public static Result AuthorizeAdminOrderRead(IRentalCallerContext caller)
    {
        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized(ErrorCodes.Unauthorized, "Yêu cầu đăng nhập.");
        }

        if (caller.IsService)
        {
            return Error.Forbidden(ErrorCodes.ServiceTokenNotAllowed, "Service token không được tra cứu đơn thuê.");
        }

        if (!caller.UserId.HasValue)
        {
            return Error.Unauthorized(ErrorCodes.Unauthorized, "Yêu cầu danh tính người dùng hợp lệ.");
        }

        if (!caller.IsAdmin())
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Chỉ quản trị viên mới được tra cứu mọi đơn thuê.");
        }

        return Result.Success();
    }

    /// <summary>
    /// Evaluates whether the caller has authority to access private evidence media for a given rental item/order.
    /// Invariants:
    /// - Caller must be platform admin, the order's renter, or the item's supplier.
    /// - Service tokens are not permitted to access private user evidence.
    /// - MediaId must be present in the item's recorded evidence collections (handover, returns, deductions, or dispute).
    /// </summary>
    public static Result AuthorizeEvidenceRead(
        IRentalCallerContext caller,
        RentalOrder order,
        RentalItem item,
        Guid mediaId)
    {
        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized(ErrorCodes.Unauthorized, "Yêu cầu đăng nhập.");
        }

        if (caller.IsService)
        {
            return Error.Forbidden(ErrorCodes.ServiceTokenNotAllowed, "Service token không được truy cập bằng chứng riêng tư.");
        }

        if (!caller.UserId.HasValue)
        {
            return Error.Unauthorized(ErrorCodes.Unauthorized, "Yêu cầu danh tính người dùng hợp lệ.");
        }

        bool isRenter = caller.UserId.Value == order.RenterId;
        bool isSupplier = caller.UserId.Value == item.SupplierId;
        bool isAdmin = caller.IsAdmin();

        if (!isRenter && !isSupplier && !isAdmin)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Không có quyền truy cập bằng chứng của khoản thuê này.");
        }

        if (!item.ContainsEvidenceMedia(mediaId))
        {
            return Error.NotFound("Media.EvidenceNotFound", "MediaId không thuộc bằng chứng của khoản thuê này.");
        }

        return Result.Success();
    }
}
