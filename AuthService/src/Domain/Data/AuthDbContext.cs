using Dprental.AuthService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Data;

public sealed class AuthDbContext : DbContext
{
    public AuthDbContext(DbContextOptions<AuthDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users { get; set; }
    public DbSet<Role> Roles { get; set; }
    public DbSet<UserRole> UserRoles { get; set; }
    public DbSet<Session> Sessions { get; set; }
    public DbSet<RefreshToken> RefreshTokens { get; set; }
    //public DbSet<AuditLog> AuditLogs { get; set; }
    public DbSet<Permission> Permissions { get; set; }
    public DbSet<RolePermission> RolePermissions { get; set; }
    public DbSet<ServicePrincipal> ServicePrincipals { get; set; }
    public DbSet<ServicePrincipalScope> ServicePrincipalScopes { get; set; }
    public DbSet<Organization> Organizations { get; set; }
    public DbSet<OrganizationMembership> OrganizationMemberships { get; set; }
}
