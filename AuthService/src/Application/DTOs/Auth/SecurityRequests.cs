using System;
using System.Collections.Generic;
using System.Text;

namespace Dprental.AuthService.Domain.DTOs.Auth;

public record ChangePasswordRequest(Guid UserId, string OldPassword, string NewPassword);
public record ResetPasswordRequest(string Email, string Token, string NewPassword);
public record RequestPasswordResetRequest(string Email);
public record RevokeSessionRequest(Guid UserId);
