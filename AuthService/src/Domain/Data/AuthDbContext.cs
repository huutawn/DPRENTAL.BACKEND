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
    public DbSet<Permission> Permissions { get; set; }
    public DbSet<RolePermission> RolePermissions { get; set; }
    public DbSet<ServicePrincipal> ServicePrincipals { get; set; }
    public DbSet<ServicePrincipalScope> ServicePrincipalScopes { get; set; }
    public DbSet<Organization> Organizations { get; set; }
    public DbSet<OrganizationMembership> OrganizationMemberships { get; set; }
    public DbSet<NavigationCatalog> NavigationCatalogs { get; set; }
    public DbSet<NavigationItem> NavigationItems { get; set; }
    public DbSet<NavigationItemPermission> NavigationItemPermissions { get; set; }
    public DbSet<ExternalLogin> ExternalLogins { get; set; }
    public DbSet<GoogleLoginTicket> GoogleLoginTickets { get; set; }
    public DbSet<AuditLog> AuditLogs { get; set; }

    public DbSet<RecoveryToken> RecoveryTokens { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<NavigationCatalog>(entity =>
        {
            entity.HasIndex(catalog => catalog.OrganizationId)
                .IsUnique()
                .HasFilter("\"OrganizationId\" IS NOT NULL");
            entity.HasIndex(catalog => catalog.DashboardType)
                .IsUnique()
                .HasFilter("\"OrganizationId\" IS NULL");
            entity.HasOne(catalog => catalog.Organization)
                .WithMany(organization => organization.NavigationCatalogs)
                .HasForeignKey(catalog => catalog.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NavigationItem>(entity =>
        {
            entity.HasOne(item => item.Catalog)
                .WithMany(catalog => catalog.Items)
                .HasForeignKey(item => item.CatalogId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.Parent)
                .WithMany(parent => parent.Children)
                .HasForeignKey(item => item.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<NavigationItemPermission>(entity =>
        {
            entity.HasOne(binding => binding.NavigationItem)
                .WithMany(item => item.Permissions)
                .HasForeignKey(binding => binding.NavigationItemId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(binding => binding.Permission)
                .WithMany()
                .HasForeignKey(binding => binding.PermissionId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
