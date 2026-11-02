namespace Dprental.AuthService.Application.DTOs.Organization;

/// <summary>Thông tin liên hệ tối thiểu của nhà cung cấp dành cho người dùng đã đăng nhập.</summary>
public sealed record SupplierContactResponse(string Name, string? ContactPhone);
