using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Access;

namespace Dprental.AuthService.Application.Mapping;

internal static class UserMappingExtensions
{
    internal static CurrentUserResponse ToCurrentUserResponse(this User user)
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
            UserAccessResolver.Roles(user),
            UserAccessResolver.Permissions(user),
            organizations,
            user.CreatedAt,
            user.IsProfileCompleted,
            user.AvatarUrl,
            user.PhoneNumber,
            user.DateOfBirth,
            CustomerAccountRules.MaskCitizenId(user.CitizenId),
            user.HasPassword);
    }
}
