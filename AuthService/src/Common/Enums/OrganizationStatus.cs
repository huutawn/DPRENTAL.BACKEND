namespace Dprental.AuthService.Common.Enums;

/// <summary>Vòng đời của organization. Chỉ organization Active mới đóng góp permission và organization context vào JWT.</summary>
public enum OrganizationStatus
{
    /// <summary>Tự đăng ký, đang chờ Platform duyệt.</summary>
    Pending,
    Active,
    Rejected,
    Suspended,
    /// <summary>Hồ sơ nháp, người dùng đang soạn thảo và chưa nộp duyệt.</summary>
    Draft
}
