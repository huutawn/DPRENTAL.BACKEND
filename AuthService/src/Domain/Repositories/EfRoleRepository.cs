using Dprental.AuthService.Domain.Data;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Repositories;

public sealed class EfRoleRepository(AuthDbContext dbContext) : IRoleRepository
{
    public Task<bool> RoleExistsAsync(Guid roleId, CancellationToken cancellationToken = default) =>
        dbContext.Roles.AsNoTracking().AnyAsync(role => role.Id == roleId, cancellationToken);

    public async Task<IReadOnlyList<string>> GetPermissionCodesAsync(Guid roleId, CancellationToken cancellationToken = default) =>
        await dbContext.RolePermissions
            .AsNoTracking()
            .Where(rolePermission => rolePermission.RoleId == roleId)
            .Select(rolePermission => rolePermission.Permission.Code)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<string>> GetUserPermissionCodesAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await dbContext.UserRoles
            .AsNoTracking()
            .Where(userRole => userRole.UserId == userId)
            .SelectMany(userRole => userRole.Role.RolePermissions.Select(rolePermission => rolePermission.Permission.Code))
            .Distinct()
            .ToListAsync(cancellationToken);

    public Task<bool> IsAssignedAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default) =>
        dbContext.UserRoles
            .AsNoTracking()
            .AnyAsync(userRole => userRole.UserId == userId && userRole.RoleId == roleId, cancellationToken);

    public async Task<bool> AssignAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default)
    {
        bool userExists = await dbContext.Users.AsNoTracking().AnyAsync(user => user.Id == userId, cancellationToken);
        bool roleExists = await dbContext.Roles.AsNoTracking().AnyAsync(role => role.Id == roleId, cancellationToken);
        if (!userExists || !roleExists)
        {
            return false;
        }

        dbContext.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId });
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RevokeAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default)
    {
        int deleted = await dbContext.UserRoles
            .Where(userRole => userRole.UserId == userId && userRole.RoleId == roleId)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
    }
}
