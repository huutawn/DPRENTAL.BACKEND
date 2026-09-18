using System;
using System.Collections.Generic;
using System.Text;

namespace AuthService.Domain.Entities
{
    internal sealed class AuditLog
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public string ActionType { get; set; } = string.Empty;
        public string? IpAddress { get; set; }
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    }
}
