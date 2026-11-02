namespace Dprental.NotificationService.Application.Policies;

public static class PreferencePolicy
{
    private static readonly HashSet<string> ValidCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "ORDER",
        "PAYMENT",
        "HANDOVER",
        "RETURN"
    };

    public static bool IsValidCategory(string category)
    {
        return !string.IsNullOrWhiteSpace(category) && ValidCategories.Contains(category);
    }

    /// <summary>
    /// Kiểm tra người dùng có bật nhận email cho category không. Mặc định là bật (true).
    /// </summary>
    public static bool IsEmailAllowed(string category, bool? userPreference)
    {
        // Safe default: true
        return userPreference ?? true;
    }
}
