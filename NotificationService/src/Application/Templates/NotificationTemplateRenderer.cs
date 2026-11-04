using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dprental.NotificationService.Common;

namespace Dprental.NotificationService.Application.Templates;

public sealed record RenderedNotification(
    string Title,
    string Body,
    string TemplateCode,
    string TemplateVersion,
    string RenderDataJson,
    string? EmailSubject = null,
    string? EmailBody = null);

public interface INotificationTemplateRenderer
{
    Result<RenderedNotification> Render(
        string notificationType,
        IDictionary<string, object?> data,
        bool isStale = false);
}

public sealed partial class NotificationTemplateRenderer : INotificationTemplateRenderer
{
    private static readonly CultureInfo ViVnCulture = new("vi-VN");
    public const string CurrentTemplateVersion = "1.0";

    [GeneratedRegex(@"\{([a-zA-Z0-9_]+)(?::([a-zA-Z0-9_\/:]+))?\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();

    public Result<RenderedNotification> Render(
        string notificationType,
        IDictionary<string, object?> data,
        bool isStale = false)
    {
        ArgumentNullException.ThrowIfNull(data);

        // Chuẩn bị dữ liệu hiển thị an toàn
        var safeData = SanitizeData(data);
        var orderCode = safeData.TryGetValue("orderCode", out var codeObj) && !string.IsNullOrWhiteSpace(codeObj?.ToString())
            ? codeObj.ToString()!
            : (safeData.TryGetValue("rentalOrderId", out var idObj) ? idObj?.ToString() : "đơn thuê");

        safeData["displayOrderCode"] = orderCode;

        string titlePattern;
        string bodyPattern;
        string? emailSubjectPattern = null;
        string? emailBodyPattern = null;

        switch (notificationType)
        {
            case "ORDER_CREATED":
                titlePattern = "Đơn thuê #{displayOrderCode} đã được tạo";
                bodyPattern = isStale
                    ? "Đơn thuê {displayOrderCode} đã được tạo và chuyển sang trạng thái xử lý tiếp theo (thông tin lưu trữ)."
                    : "Đơn thuê {displayOrderCode} đang chờ thanh toán {totalPayableAmount:N0} VND trước {paymentDeadline:HH:mm dd/MM/yyyy}.";
                break;

            case "ORDER_PAID":
                titlePattern = "Thanh toán thành công đơn thuê #{displayOrderCode}";
                bodyPattern = "Đơn thuê {displayOrderCode} đã thanh toán thành công {paidAmount:N0} VND. Nhà cung cấp sẽ chuẩn bị thiết bị để bàn giao.";
                emailSubjectPattern = "[DPRental] Xác nhận thanh toán thành công đơn thuê #{displayOrderCode}";
                emailBodyPattern = "<p>Kính gửi Quý khách,</p><p>DPRental xác nhận đã nhận thanh toán số tiền <strong>{paidAmount:N0} VND</strong> cho đơn thuê <strong>{displayOrderCode}</strong>.</p><p>Mã hóa đơn: {invoiceRef}. Nhà cung cấp đang chuẩn bị bàn giao máy theo lịch hẹn.</p>";
                break;

            case "SUPPLIER_CONFIRMATION_REQUIRED":
                titlePattern = "Yêu cầu xác nhận đơn thuê mới #{displayOrderCode}";
                bodyPattern = isStale
                    ? "Yêu cầu xác nhận đơn thuê {displayOrderCode} đã kết thúc hoặc được chuyển tiếp (thông tin lưu trữ)."
                    : "Bạn có yêu cầu thuê thiết bị mới cho đơn hàng {displayOrderCode}. Vui lòng xác nhận trước {supplierConfirmationDeadline:HH:mm dd/MM/yyyy}.";
                emailSubjectPattern = "[DPRental] Yêu cầu xác nhận đơn thuê #{displayOrderCode}";
                emailBodyPattern = "<p>Kính gửi Quý Đối tác,</p><p>Bạn có yêu cầu thuê thiết bị mới cho đơn hàng <strong>{displayOrderCode}</strong>.</p><p>Vui lòng đăng nhập hệ thống và xác nhận trước <strong>{supplierConfirmationDeadline:HH:mm dd/MM/yyyy}</strong>.</p>";
                break;

            case "ORDER_EXPIRED":
                titlePattern = "Đơn thuê #{displayOrderCode} đã hết hạn";
                bodyPattern = "Đơn thuê {displayOrderCode} đã hết hạn do quá thời gian quy định ({reason}). Thiết bị đã được giải phóng.";
                break;

            case "ORDER_CANCELLED":
                titlePattern = "Đơn thuê #{displayOrderCode} đã bị hủy";
                bodyPattern = "Đơn thuê {displayOrderCode} đã bị hủy vì lý do: {reason}. Các khoản tiền liên quan sẽ được xử lý theo chính sách hoàn tiền.";
                break;

            case "HANDOVER_COMPLETED":
                titlePattern = "Bàn giao thiết bị thành công cho đơn thuê #{displayOrderCode}";
                bodyPattern = "Thiết bị trong đơn thuê {displayOrderCode} đã được bàn giao thành công lúc {actualHandoverAt:HH:mm dd/MM/yyyy}. Chúc Quý khách có trải nghiệm thuê tốt đẹp.";
                break;

            case "HANDOVER_OVERDUE_SUPPLIER":
                titlePattern = "CẢNH BÁO: Quá hạn bàn giao thiết bị đơn thuê #{displayOrderCode}";
                bodyPattern = "Đơn thuê {displayOrderCode} đã quá hạn bàn giao dự kiến ({expectedStartDate:dd/MM/yyyy}). Vui lòng tiến hành bàn giao ngay hoặc liên hệ hỗ trợ.";
                emailSubjectPattern = "[Khẩn cấp] Đơn thuê #{displayOrderCode} đã quá hạn bàn giao thiết bị";
                emailBodyPattern = "<p>Kính gửi Quý Đối tác,</p><p>Đơn thuê <strong>{displayOrderCode}</strong> đã quá hạn bàn giao thiết bị dự kiến vào ngày <strong>{expectedStartDate:dd/MM/yyyy}</strong>.</p><p>Vui lòng hoàn tất bàn giao thiết bị ngay hoặc liên hệ bộ phận hỗ trợ DPRental.</p>";
                break;

            case "HANDOVER_OVERDUE_RENTER":
                titlePattern = "Thông báo chậm trễ bàn giao đơn thuê #{displayOrderCode}";
                bodyPattern = "Đơn thuê {displayOrderCode} hiện chưa được bàn giao theo lịch hẹn ({expectedStartDate:dd/MM/yyyy}). DPRental đang đôn đốc nhà cung cấp xử lý.";
                break;

            case "RETURN_CONFIRMATION_OVERDUE_SUPPLIER":
                titlePattern = "CẢNH BÁO: Quá hạn kiểm tra thiết bị hoàn trả đơn thuê #{displayOrderCode}";
                bodyPattern = "Đã hết hạn xác nhận hoàn trả ({confirmationDeadlineAt:HH:mm dd/MM/yyyy}) cho đơn thuê {displayOrderCode}. Vui lòng hoàn tất kiểm tra tình trạng máy.";
                emailSubjectPattern = "[Khẩn cấp] Quá hạn xác nhận hoàn trả thiết bị đơn thuê #{displayOrderCode}";
                emailBodyPattern = "<p>Kính gửi Quý Đối tác,</p><p>Đã hết hạn thời gian xác nhận hoàn trả vào lúc <strong>{confirmationDeadlineAt:HH:mm dd/MM/yyyy}</strong> cho đơn thuê <strong>{displayOrderCode}</strong>.</p><p>Vui lòng truy cập hệ thống để hoàn tất kiểm tra tình trạng máy và xác nhận.</p>";
                break;

            case "RETURN_CONFIRMATION_OVERDUE_RENTER":
                titlePattern = "Đang chờ nhà cung cấp xác nhận hoàn trả đơn thuê #{displayOrderCode}";
                bodyPattern = "Nhà cung cấp đang chậm trễ kiểm tra thiết bị hoàn trả cho đơn thuê {displayOrderCode}. Hệ thống sẽ tự động hoàn tất nếu nhà cung cấp không phản hồi.";
                break;

            case "ORDER_COMPLETED":
                titlePattern = "Đơn thuê #{displayOrderCode} đã hoàn tất";
                bodyPattern = "Đơn thuê {displayOrderCode} đã hoàn tất toàn bộ chu kỳ thuê và quyết toán cọc vào lúc {completedAt:HH:mm dd/MM/yyyy}. Cảm ơn Quý khách!";
                emailSubjectPattern = "[DPRental] Đơn thuê #{displayOrderCode} đã hoàn tất chu kỳ thuê";
                emailBodyPattern = "<p>Kính gửi Quý khách,</p><p>Đơn thuê <strong>{displayOrderCode}</strong> đã hoàn tất thành công chu kỳ thuê vào lúc <strong>{completedAt:HH:mm dd/MM/yyyy}</strong>.</p><p>Tiền cọc và các chi phí phát sinh (nếu có) đã được quyết toán. Cảm ơn Quý khách đã tin tưởng sử dụng DPRental!</p>";
                break;

            case "DEDUCTION_DISPUTE_RESOLVED_RENTER":
                titlePattern = "Khiếu nại khấu trừ cọc đã được giải quyết";
                bodyPattern = "Quản trị viên đã chốt mức khấu trừ {finalDeductionAmount:N0} VND cho đơn thuê {displayOrderCode}. Xem chi tiết quyết định trong đơn. Việc hoàn tiền sẽ được xử lý riêng.";
                break;

            case "DEDUCTION_DISPUTE_RESOLVED_SUPPLIER":
                titlePattern = "Khiếu nại khấu trừ cọc đã được giải quyết";
                bodyPattern = "Quản trị viên đã chốt mức khấu trừ {finalDeductionAmount:N0} VND cho đơn thuê {displayOrderCode}. Xem chi tiết quyết định trong đơn.";
                break;

            case "LATE_PAYMENT_REFUND_REQUESTED":
                // BẤT BIẾN TÀI CHÍNH: Chỉ được khẳng định yêu cầu hoàn tiền đang được xử lý, TUYỆT ĐỐI không khẳng định đã hoàn tiền thành công!
                titlePattern = "Yêu cầu hoàn tiền đơn thuê #{displayOrderCode}";
                bodyPattern = "Khoản thanh toán {totalRefundAmount:N0} VND cho đơn thuê {displayOrderCode} (do thanh toán sau khi đơn đã kết thúc) đang được xử lý hoàn tiền. Vui lòng theo dõi tiến độ xử lý tại chi tiết đơn.";
                break;

            default:
                return Result.Failure<RenderedNotification>(
                    Error.Validation("UNKNOWN_TEMPLATE", $"Không tìm thấy template tương ứng cho '{notificationType}'."));
        }

        var renderedTitle = FormatString(titlePattern, safeData);
        var renderedBody = FormatString(bodyPattern, safeData);
        var renderedEmailSubject = emailSubjectPattern != null ? FormatString(emailSubjectPattern, safeData) : null;
        var renderedEmailBody = emailBodyPattern != null ? FormatString(emailBodyPattern, safeData) : null;

        // Lưu trữ dữ liệu render tối thiểu đã chuẩn hóa, không lưu raw Kafka payload
        var minimalDataJson = JsonSerializer.Serialize(safeData);

        return Result.Success(new RenderedNotification(
            Title: renderedTitle,
            Body: renderedBody,
            TemplateCode: notificationType,
            TemplateVersion: CurrentTemplateVersion,
            RenderDataJson: minimalDataJson,
            EmailSubject: renderedEmailSubject,
            EmailBody: renderedEmailBody));
    }

    private static Dictionary<string, object?> SanitizeData(IDictionary<string, object?> source)
    {
        var sanitized = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in source)
        {
            if (kvp.Value is string strVal)
            {
                sanitized[kvp.Key] = WebUtility.HtmlEncode(strVal);
            }
            else
            {
                sanitized[kvp.Key] = kvp.Value;
            }
        }

        return sanitized;
    }

    private static string FormatString(string pattern, Dictionary<string, object?> data)
    {
        return PlaceholderRegex().Replace(pattern, match =>
        {
            var key = match.Groups[1].Value;
            var format = match.Groups[2].Success ? match.Groups[2].Value : null;

            if (!data.TryGetValue(key, out var val) || val == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrEmpty(format))
            {
                if (val is IFormattable formattable)
                {
                    return formattable.ToString(format, ViVnCulture);
                }
                if (DateTimeOffset.TryParse(val.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
                {
                    return dto.ToString(format, ViVnCulture);
                }
                if (DateTime.TryParse(val.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                {
                    return dt.ToString(format, ViVnCulture);
                }
                if (decimal.TryParse(val.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var dec))
                {
                    return dec.ToString(format, ViVnCulture);
                }
            }

            return val.ToString() ?? string.Empty;
        });
    }
}
