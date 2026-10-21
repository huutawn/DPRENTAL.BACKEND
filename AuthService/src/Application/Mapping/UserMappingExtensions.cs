using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Security;
using Dprental.AuthService.Common.Utils;

namespace Dprental.AuthService.Application.Mapping;

internal static class UserMappingExtensions
{
    internal static CurrentUserResponse ToCurrentUserResponse(this User user, string? avatarUrl = null)
    {
        ArgumentNullException.ThrowIfNull(user);

        UserOrganizationResponse[] organizations = UserAccessResolver.ActiveMemberships(user)
            .Select(membership => new UserOrganizationResponse(
                membership.OrganizationId,
                membership.Organization.Name,
                membership.Organization.Slug,
                membership.Organization.Type,
                membership.Role.Name))
            .ToArray();

        return new CurrentUserResponse(
            user.Id,
            user.Email,
            user.FullName,
            UserAccessResolver.EffectiveRoles(user),
            UserAccessResolver.Permissions(user),
            organizations,
            user.CreatedAt,
            user.IsProfileCompleted,
            user.AvatarMediaId,
            avatarUrl,
            user.PhoneNumber,
            user.DateOfBirth,
            StringUtility.MaskCitizenId(user.CitizenId),
            user.HasPassword);
    }
}
