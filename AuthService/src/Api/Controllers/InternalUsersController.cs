using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Domain.IRepositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.AuthService.Api.Controllers;

/// <summary>
/// Endpoint nội bộ phục vụ giao tiếp giữa các service (được bảo vệ bởi InternalApiKeyMiddleware với header X-Api-Key).
/// </summary>
[ApiController]
[Route("internal/users")]
[AllowAnonymous]
public sealed class InternalUsersController(IUserRepository userRepository) : ControllerBase
{
    /// <summary>
    /// Tra cứu thông tin liên hệ hàng loạt (email, isActive, isEmailVerified) cho tối đa 100 user GUIDs.
    /// </summary>
    [HttpPost("notification-contacts:batch")]
    [ProducesResponseType(typeof(NotificationContactBatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetNotificationContactsBatch(
        [FromBody] NotificationContactBatchRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request?.UserIds == null)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Bad Request",
                Detail = "userIds không được để trống."
            });
        }

        var distinctIds = request.UserIds.Where(id => id != Guid.Empty).Distinct().Take(100).ToList();
        var users = await userRepository.GetUsersByIdsAsync(distinctIds, cancellationToken);

        var contacts = users.Select(u => new NotificationContactDto(
            UserId: u.Id,
            Email: u.Email,
            IsActive: u.IsActive,
            IsEmailVerified: true
        )).ToList();

        return Ok(new NotificationContactBatchResponse(contacts));
    }
}
