using System;
using System.Collections.Generic;
using System.Text;

namespace Dprental.AuthService.Domain.Entities;

public sealed class AuditLog
{
    public Guid Id { get; set; }
    public Guid? ActorId { get; set; }
    public string Target { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}