namespace Dprental.RentalService.Application.Security;

/// <summary>
/// Chuẩn hóa tên SignalR group theo chuẩn danh tính người dùng GUID chữ thường (:D).
/// </summary>
public static class OrderHubGroupNames
{
    public static string Renter(Guid userId) => $"renter:{userId:D}";
    public static string Supplier(Guid userId) => $"supplier:{userId:D}";
}
