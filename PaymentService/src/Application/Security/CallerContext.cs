using System;
using System.Collections.Generic;

namespace Dprental.PaymentService.Application.Security;

/// <summary>
/// Ngữ cảnh caller thực thi nghiệp vụ, được ánh xạ từ ClaimsPrincipal tại tầng API/Transport.
/// Tuyệt đối không đưa HttpContext hay ClaimsPrincipal vào Application use cases.
/// </summary>
public sealed record CallerContext(
    string CallerId,
    string CallerType,
    IReadOnlyList<string>? Scopes = null)
{
    public string ToActor() => $"{CallerType.ToUpperInvariant()}:{CallerId.Trim()}";

    public static readonly CallerContext System = new("SYSTEM", "system");
    public static readonly CallerContext Webhook = new("SEPAY", "webhook");

    public static CallerContext ForService(string serviceId, IReadOnlyList<string>? scopes = null) =>
        new(serviceId, "service", scopes);

    public static CallerContext ForAdmin(Guid adminId) =>
        new(adminId.ToString("D"), "admin");

    public static CallerContext ForRenter(Guid renterId) =>
        new(renterId.ToString("D"), "renter");
}
