namespace Dprental.AuthService.Application.Options;

/// <summary>Mốc thời gian của việc duyệt hồ sơ organization và việc Owner nhắc duyệt.</summary>
public sealed class OrganizationReviewOptions
{
    public const string SectionName = "OrganizationReview";

    /// <summary>Hạn Platform duyệt hồ sơ tính từ lúc nộp (giờ). Quá hạn này Owner mới được nhắc duyệt.</summary>
    public int SlaHours { get; set; } = 48;

    /// <summary>Khoảng cách tối thiểu giữa hai lần nhắc duyệt (giờ).</summary>
    public int ReminderCooldownHours { get; set; } = 24;
}
