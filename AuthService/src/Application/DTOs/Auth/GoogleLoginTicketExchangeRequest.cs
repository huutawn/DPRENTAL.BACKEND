using System.ComponentModel.DataAnnotations;

namespace Dprental.AuthService.Application.DTOs.Auth;

/// <summary>Request từ Next BFF để đổi ticket Google một lần lấy cặp token.</summary>
public sealed record GoogleLoginTicketExchangeRequest(
    [Required, StringLength(500)] string? Ticket);
