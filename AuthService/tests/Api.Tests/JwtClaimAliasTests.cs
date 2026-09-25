using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Dprental.AuthService.Api.Security;
using Dprental.AuthService.Application.DependencyInjection;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Security.Jwt;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.DTOs.User;

using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Models;
using Dprental.AuthService.Common.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class JwtClaimAliasTests
{
    private const string BootstrapKey = "bootstrap-key-for-jwt-alias-tests";

    [Fact]
    public void IssueProducesShortClaimsAndAliasesWithoutLegacyClaims()
    {
        JwtOptions options = CreateValidOptions();
        using RsaJwtKeyRing keyRing = new(options);
        DateTimeOffset now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        RsaAccessTokenIssuer issuer = new(Options.Create(options), keyRing, new FixedTimeProvider(now));
        Guid subjectId = Guid.NewGuid();
        Guid orgId = Guid.NewGuid();

        AccessTokenRequest request = new(
            SubjectId: subjectId,
            Roles: [SystemRoleCode.Admin, SystemRoleCode.AdminPlatform, SystemRoleCode.Owner, SystemRoleCode.Member],
            Permissions: [PermissionCode.IdentityUsersRead, PermissionCode.IdentityRolesAssignGlobal],
            OrganizationIds: [orgId]);

        IssuedAccessToken token = issuer.Issue(request);
        JwtSecurityToken jwt = new JwtSecurityTokenHandler().ReadJwtToken(token.Value);

        // Standard headers & claims
        Assert.Equal(SecurityAlgorithms.RsaSha256, jwt.Header.Alg);
        Assert.Equal(options.ActiveKeyId, jwt.Header.Kid);
        Assert.Equal(subjectId.ToString("D"), jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.NotNull(jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti));
        Assert.NotNull(jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Iat));

        // Short claim names used
        List<string> roleClaims = jwt.Claims.Where(c => c.Type == JwtClaimCatalog.Claims.Role).Select(c => c.Value).ToList();
        Assert.Contains(JwtClaimCatalog.RoleAliases.Admin, roleClaims);
        Assert.Contains(JwtClaimCatalog.RoleAliases.AdminPlatform, roleClaims);
        Assert.Contains(JwtClaimCatalog.RoleAliases.Owner, roleClaims);
        Assert.Contains(JwtClaimCatalog.RoleAliases.Member, roleClaims);

        List<string> permClaims = jwt.Claims.Where(c => c.Type == JwtClaimCatalog.Claims.Permission).Select(c => c.Value).ToList();
        Assert.Contains(JwtClaimCatalog.PermissionAliases.IdentityUsersRead, permClaims);
        Assert.Contains(JwtClaimCatalog.PermissionAliases.IdentityRolesAssignGlobal, permClaims);

        // Context claim "o"
        Assert.Equal(orgId.ToString("D"), jwt.Claims.First(c => c.Type == JwtClaimCatalog.Claims.Organization).Value);

        // Legacy claims MUST NOT exist
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "role");
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "perm");
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "org");
    }

    [Fact]
    public void ServiceTokenProducesShortUAndSClaims()
    {
        JwtOptions options = CreateValidOptions();
        using RsaJwtKeyRing keyRing = new(options);
        DateTimeOffset now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        RsaAccessTokenIssuer issuer = new(Options.Create(options), keyRing, new FixedTimeProvider(now));
        Guid serviceId = Guid.NewGuid();

        AccessTokenRequest request = new(
            SubjectId: serviceId,
            TokenUse: "service",
            Scopes: ["orders:read", "rentals:manage"]);

        IssuedAccessToken token = issuer.Issue(request);
        JwtSecurityToken jwt = new JwtSecurityTokenHandler().ReadJwtToken(token.Value);

        Assert.Equal(JwtClaimCatalog.TokenUses.Service, jwt.Claims.First(c => c.Type == JwtClaimCatalog.Claims.TokenUse).Value);
        List<string> scopes = jwt.Claims.Where(c => c.Type == JwtClaimCatalog.Claims.Scope).Select(c => c.Value).ToList();
        Assert.Contains("orders:read", scopes);
        Assert.Contains("rentals:manage", scopes);

        // Legacy claims MUST NOT exist
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "token_use");
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "scope");
    }

    [Fact]
    public async Task PolicyAuthorizesTokenWithAliasedPermissionAndRejectsLegacyClaim()
    {
        ServiceCollection services = [];
        JwtOptions options = CreateValidOptions();
        Dictionary<string, string?> configValues = new()
        {
            ["Auth:Jwt:Issuer"] = options.Issuer,
            ["Auth:Jwt:Audience"] = options.Audience,
            ["Auth:Jwt:AccessTokenLifetimeMinutes"] = options.AccessTokenLifetimeMinutes.ToString(CultureInfo.InvariantCulture),
            ["Auth:Jwt:ActiveKeyId"] = options.ActiveKeyId,
            ["Auth:Jwt:Keys:0:KeyId"] = options.ActiveKeyId,
            ["Auth:Jwt:Keys:0:PublicKeyPem"] = options.Keys[0].PublicKeyPem,
            ["Auth:Jwt:Keys:0:PrivateKeyPem"] = options.Keys[0].PrivateKeyPem,
            ["Auth:AdminBootstrap:CreationKey"] = BootstrapKey
        };
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        services.AddLogging();
        services.AddAuthApplication(config);
        services.AddJwtBearerSecurity();

        using ServiceProvider provider = services.BuildServiceProvider();
        IAuthorizationService authService = provider.GetRequiredService<IAuthorizationService>();

        // 1. Token with aliased permission claim "p" = "id.r.ag"
        ClaimsIdentity aliasedIdentity = new(
            [new Claim(JwtClaimCatalog.Claims.Permission, JwtClaimCatalog.PermissionAliases.IdentityRolesAssignGlobal)],
            "TestAuth");
        ClaimsPrincipal aliasedPrincipal = new(aliasedIdentity);

        AuthorizationResult resultValid = await authService.AuthorizeAsync(
            aliasedPrincipal,
            null,
            PermissionCode.IdentityRolesAssignGlobal);
        Assert.True(resultValid.Succeeded);

        // 2. Token with legacy permission claim "perm" = "roles.assign" -> MUST BE REJECTED
        ClaimsIdentity legacyIdentity1 = new(
            [new Claim("perm", "roles.assign")],
            "TestAuth");
        AuthorizationResult resultLegacy1 = await authService.AuthorizeAsync(
            new ClaimsPrincipal(legacyIdentity1),
            null,
            PermissionCode.IdentityRolesAssignGlobal);
        Assert.False(resultLegacy1.Succeeded);

        // 3. Token with unaliased canonical code in "p" -> MUST BE REJECTED (must use alias)
        ClaimsIdentity canonicalIdentity = new(
            [new Claim(JwtClaimCatalog.Claims.Permission, PermissionCode.IdentityRolesAssignGlobal)],
            "TestAuth");
        AuthorizationResult resultCanonical = await authService.AuthorizeAsync(
            new ClaimsPrincipal(canonicalIdentity),
            null,
            PermissionCode.IdentityRolesAssignGlobal);
        Assert.False(resultCanonical.Succeeded);
    }

    [Fact]
    public async Task RolesControllerAssignRejectsLegacyTokensAndAcceptsAliasedTokens()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        await SeedDatabaseAsync(factory);

        IAccessTokenIssuer issuer = factory.Services.GetRequiredService<IAccessTokenIssuer>();
        Guid actorAdminId = Guid.NewGuid();
        Guid targetUserId = Guid.NewGuid();
        Guid globalRoleId = new("fba7cef9-3ad7-49f5-8c2b-f9364945fcc4");

        // Seed actor user in DB
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.Users.Add(new User { Id = actorAdminId, Email = "actor@example.com", FullName = "Actor", IsActive = true });
            db.Users.Add(new User { Id = targetUserId, Email = "target@example.com", FullName = "Target", IsActive = true });
            // Give actor all permissions in user_roles so service guard succeeds
            Role adminRole = await db.Roles.Include(r => r.RolePermissions).FirstAsync(r => r.Id == globalRoleId);
            db.UserRoles.Add(new UserRole { UserId = actorAdminId, RoleId = adminRole.Id });
            await db.SaveChangesAsync();
        }

        using HttpClient client = factory.CreateClient();

        // 1. Request with token issued via RsaAccessTokenIssuer (contains "p" = "id.r.ag")
        IssuedAccessToken validToken = issuer.Issue(new AccessTokenRequest(
            SubjectId: actorAdminId,
            Roles: [SystemRoleCode.Admin],
            Permissions: [PermissionCode.IdentityRolesAssignGlobal]));

        HttpRequestMessage validReq = new(HttpMethod.Post, "/api/v1/auth/roles/assign")
        {
            Content = JsonContent.Create(new AssignRoleRequest(targetUserId, globalRoleId))
        };
        validReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", validToken.Value);

        using HttpResponseMessage validRes = await client.SendAsync(validReq);
        Assert.Equal(HttpStatusCode.NoContent, validRes.StatusCode);

        // 2. Request with forged legacy token containing perm = "roles.assign"
        RsaJwtKeyRing factoryKeyRing = factory.Services.GetRequiredService<RsaJwtKeyRing>();
        IOptions<JwtOptions> factoryJwtOptions = factory.Services.GetRequiredService<IOptions<JwtOptions>>();
        string legacyToken = CreateCustomToken(
            actorAdminId,
            [new Claim("role", "admin"), new Claim("perm", "roles.assign")],
            factoryJwtOptions.Value,
            factoryKeyRing);

        HttpRequestMessage legacyReq = new(HttpMethod.Post, "/api/v1/auth/roles/assign")
        {
            Content = JsonContent.Create(new AssignRoleRequest(targetUserId, globalRoleId))
        };
        legacyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", legacyToken);

        using HttpResponseMessage legacyRes = await client.SendAsync(legacyReq);
        // Policy identity.roles.assign.global rejects legacy token!
        Assert.Equal(HttpStatusCode.Forbidden, legacyRes.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        string inMemoryDatabaseName = Guid.NewGuid().ToString("N");
        using RSA rsa = RSA.Create(2048);
        Dictionary<string, string?> settings = new()
        {
            ["Auth:Jwt:Issuer"] = "https://auth.example.test",
            ["Auth:Jwt:Audience"] = "dprental-clients",
            ["Auth:Jwt:AccessTokenLifetimeMinutes"] = "15",
            ["Auth:Jwt:ActiveKeyId"] = "current",
            ["Auth:Jwt:Keys:0:KeyId"] = "current",
            ["Auth:Jwt:Keys:0:PublicKeyPem"] = rsa.ExportRSAPublicKeyPem(),
            ["Auth:Jwt:Keys:0:PrivateKeyPem"] = rsa.ExportRSAPrivateKeyPem(),
            ["Auth:AdminBootstrap:CreationKey"] = BootstrapKey
        };

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AuthDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AuthDbContext>>();
                services.AddDbContext<AuthDbContext>(options => options.UseInMemoryDatabase(inMemoryDatabaseName));
            });
        });
    }

    private static async Task SeedDatabaseAsync(WebApplicationFactory<Program> factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        Role adminRole = new() { Id = new Guid("fba7cef9-3ad7-49f5-8c2b-f9364945fcc4"), Name = SystemRoleCode.Admin, Scope = RoleScope.Global };
        Permission pAssign = new() { Id = Guid.NewGuid(), Code = PermissionCode.IdentityRolesAssignGlobal };
        adminRole.RolePermissions.Add(new RolePermission { Role = adminRole, Permission = pAssign });
        db.Roles.Add(adminRole);
        db.Permissions.Add(pAssign);
        await db.SaveChangesAsync();
    }

    private static string CreateCustomToken(Guid sub, IEnumerable<Claim> extraClaims, JwtOptions options, RsaJwtKeyRing keyRing)
    {
        List<Claim> claims = [new Claim(JwtRegisteredClaimNames.Sub, sub.ToString("D")), .. extraClaims];
        SigningCredentials credentials = new(keyRing.ActiveSigningKey, SecurityAlgorithms.RsaSha256);
        JwtSecurityToken token = new(options.Issuer, options.Audience, claims, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(15), credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static JwtOptions CreateValidOptions()
    {
        using RSA rsa = RSA.Create(2048);
        string privateKeyPem = rsa.ExportRSAPrivateKeyPem();
        string publicKeyPem = rsa.ExportRSAPublicKeyPem();

        return new JwtOptions
        {
            Issuer = "https://auth.example.test",
            Audience = "dprental-clients",
            AccessTokenLifetimeMinutes = 15,
            ActiveKeyId = "current",
            Keys = [new JwtKeyOptions { KeyId = "current", PublicKeyPem = publicKeyPem, PrivateKeyPem = privateKeyPem }]
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
