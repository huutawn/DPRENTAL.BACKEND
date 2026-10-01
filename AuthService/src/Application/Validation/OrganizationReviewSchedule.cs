using Dprental.AuthService.Application.Options;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Application.Validation;

/// <summary>
/// Tính các mốc thời gian duyệt của một hồ sơ đang Pending: nộp lúc nào, hạn duyệt, khi nào Owner được nhắc.
/// Hồ sơ ở trạng thái khác không có hạn duyệt nên các mốc này trả null.
/// </summary>
public static class OrganizationReviewSchedule
{
    /// <summary>Lần nộp gần nhất: lúc gửi lại sau khi bị từ chối, hoặc lúc đăng ký nếu chưa từng gửi lại.</summary>
    public static DateTime SubmittedAtUtc(Organization organization)
    {
        ArgumentNullException.ThrowIfNull(organization);
        return organization.ResubmittedAtUtc ?? organization.CreatedAtUtc;
    }

    public static DateTime? ReviewDueAtUtc(Organization organization, OrganizationReviewOptions options)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(options);
        return organization.Status == OrganizationStatus.Pending
            ? SubmittedAtUtc(organization).AddHours(options.SlaHours)
            : null;
    }

    /// <summary>Sớm nhất là khi quá hạn duyệt; đã nhắc rồi thì phải cách lần nhắc trước đủ thời gian chờ.</summary>
    public static DateTime? CanRemindAtUtc(Organization organization, OrganizationReviewOptions options)
    {
        if (ReviewDueAtUtc(organization, options) is not DateTime dueAtUtc)
        {
            return null;
        }

        DateTime? afterCooldown = organization.LastRemindedAtUtc?.AddHours(options.ReminderCooldownHours);
        return afterCooldown > dueAtUtc ? afterCooldown : dueAtUtc;
    }

    public static bool IsOverdue(Organization organization, OrganizationReviewOptions options, DateTime utcNow) =>
        ReviewDueAtUtc(organization, options) is DateTime dueAtUtc && utcNow > dueAtUtc;
}
