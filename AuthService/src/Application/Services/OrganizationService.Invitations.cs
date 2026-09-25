using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.Execution;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Mapping;
using Dprental.AuthService.Application.Repositories;
using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Application.Validation;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Dprental.AuthService.Application.Services;

public sealed partial class OrganizationService
{
    public async Task<Result> AcceptInvitationAsync(
        Guid actorUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(organizationId, nameof(organizationId), out _) ||
            !DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out _))
        {
            return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "AcceptOrganizationInvitation",
            async () =>
            {
                User? actor = await userRepository.GetByIdAsync(actorUserId, cancellationToken);
                if (actor is null || !actor.IsActive)
                {
                    return Result.Failure(AuthErrors.Organization.UserNotFound);
                }

                if (ProfileCompletionGuard.Check(actor) is Error profileError)
                {
                    return Result.Failure(profileError);
                }

                Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
                if (org is null)
                {
                    return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
                }

                IReadOnlyList<string> globalPerms = await GetGlobalPermissionsAsync(actorUserId, cancellationToken);
                bool canManageSuspended = globalPerms.Contains(PermissionCode.IdentityOrganizationsSuspend, StringComparer.Ordinal);

                if (org.Status != OrganizationStatus.Active && !canManageSuspended)
                {
                    return Result.Failure(InactiveError(org));
                }

                OrganizationMembership? membership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
                if (membership is null)
                {
                    return Result.Failure(AuthErrors.Organization.MembershipNotFound);
                }

                if (membership.Status != MembershipStatus.Invited)
                {
                    return Result.Failure(AuthErrors.Organization.InvalidStateTransition);
                }

                membership.Status = MembershipStatus.Active;
                membership.JoinedAtUtc = DateTime.UtcNow;

                await membershipRepository.UpdateAsync(membership, cancellationToken);
                return Result.Success();
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result<IReadOnlyList<MyOrganizationResponse>>> GetMyOrganizationsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out _))
        {
            return Result.Failure<IReadOnlyList<MyOrganizationResponse>>(AuthErrors.User.NotFound);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "GetMyOrganizations",
            async () =>
            {
                IReadOnlyList<OrganizationMembership> memberships = await membershipRepository.GetMembershipsForUserAsync(userId, cancellationToken);

                IReadOnlyList<MyOrganizationResponse> result = memberships
                    .Where(m => m.Status == MembershipStatus.Active)
                    .OrderBy(m => m.JoinedAtUtc)
                    .ThenBy(m => m.Id)
                    .Select(m => new MyOrganizationResponse(
                        m.Id,
                        m.OrganizationId,
                        m.Organization?.Name ?? string.Empty,
                        m.Organization?.Slug ?? string.Empty,
                        m.Organization?.Type ?? OrganizationType.Supplier,
                        m.Organization?.Status ?? OrganizationStatus.Active,
                        m.Role?.Name ?? string.Empty,
                        m.Status,
                        m.JoinedAtUtc))
                    .ToList();

                return Result.Success(result);
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result<IReadOnlyList<OrganizationInvitationResponse>>> GetMyInvitationsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out _))
        {
            return Result.Failure<IReadOnlyList<OrganizationInvitationResponse>>(AuthErrors.User.NotFound);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "GetMyInvitations",
            async () =>
            {
                IReadOnlyList<OrganizationMembership> memberships = await membershipRepository.GetMembershipsForUserAsync(userId, cancellationToken);

                IReadOnlyList<OrganizationInvitationResponse> result = memberships
                    .Where(m => m.Status == MembershipStatus.Invited)
                    .OrderByDescending(m => m.JoinedAtUtc)
                    .ThenBy(m => m.Id)
                    .Select(m => new OrganizationInvitationResponse(
                        m.Id,
                        m.OrganizationId,
                        m.Organization?.Name ?? string.Empty,
                        m.Organization?.Slug ?? string.Empty,
                        m.Organization?.Type ?? OrganizationType.Supplier,
                        m.Role?.Name ?? string.Empty,
                        m.JoinedAtUtc))
                    .ToList();

                return Result.Success(result);
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result> DeclineInvitationAsync(
        Guid actorUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(organizationId, nameof(organizationId), out _) ||
            !DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out _))
        {
            return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "DeclineOrganizationInvitation",
            async () =>
            {
                Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
                if (org is null)
                {
                    return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
                }

                OrganizationMembership? membership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
                if (membership is null)
                {
                    return Result.Failure(AuthErrors.Organization.MembershipNotFound);
                }

                if (membership.Status != MembershipStatus.Invited)
                {
                    return Result.Failure(AuthErrors.Organization.InvalidStateTransition);
                }

                await membershipRepository.DeleteAsync(membership.Id, cancellationToken);
                return Result.Success();
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result> LeaveAsync(
        Guid actorUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(organizationId, nameof(organizationId), out _) ||
            !DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out _))
        {
            return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "LeaveOrganization",
            async () =>
            {
                Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
                if (org is null)
                {
                    return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
                }

                OrganizationMembership? membership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
                if (membership is null || membership.Status != MembershipStatus.Active)
                {
                    return Result.Failure(AuthErrors.Organization.MembershipNotFound);
                }

                Role? currentRole = membership.Role ?? await roleRepository.GetByIdAsync(membership.RoleId, cancellationToken);
                if (IsOwner(currentRole))
                {
                    await using var transaction = await organizationRepository.BeginTransactionAsync(cancellationToken);
                    await organizationRepository.LockOrganizationsAsync([organizationId], cancellationToken);
                    int activeOwners = await membershipRepository.CountActiveOwnersAsync(organizationId, cancellationToken);
                    if (activeOwners <= 1)
                    {
                        return Result.Failure(AuthErrors.Organization.LastOwner);
                    }

                    await membershipRepository.DeleteAsync(membership.Id, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return Result.Success();
                }

                await membershipRepository.DeleteAsync(membership.Id, cancellationToken);
                return Result.Success();
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

}
