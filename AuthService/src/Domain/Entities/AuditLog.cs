using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Entities;

/// <summary>Nhật ký hành vi bảo mật/RBAC — tra theo actor (ai làm) và theo thời gian (gần đây/dọn log cũ) là 2 truy vấn chính.</summary>
[Index(nameof(ActorId))]
[Index(nameof(Timestamp))]
[Index(nameof(Target), nameof(Timestamp))]
public sealed class AuditLog
{
    public Guid Id { get; set; }
    public Guid? ActorId { get; set; }
    public string Target { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}