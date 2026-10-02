using System.Net.Http.Headers;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dprental.CatalogService.Api.Tests;

/// <summary>
/// Host CatalogService cho test equipment type. Xác thực dùng JWT thật ký bằng <see cref="TestJwtHelper"/>.
/// Mặc định dùng database InMemory; truyền connection string để chạy trên PostgreSQL thật.
/// </summary>
internal sealed class EquipmentTypeApiFactory(string? postgresConnectionString = null) : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString("N");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(
            "ConnectionStrings:Postgres",
            postgresConnectionString ?? "Host=localhost;Database=test;Username=test;Password=test");
        builder.UseSetting("Auth:Jwt:Issuer", TestJwtHelper.DefaultIssuer);
        builder.UseSetting("Auth:Jwt:Audience", TestJwtHelper.DefaultAudience);
        builder.UseSetting("Auth:Jwt:Keys:0:KeyId", TestJwtHelper.DefaultKeyId);
        builder.UseSetting("Auth:Jwt:Keys:0:PublicKeyPem", TestJwtHelper.PublicKeyPem);

        if (postgresConnectionString is null)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<CatalogServiceDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<CatalogServiceDbContext>>();
                services.AddDbContext<CatalogServiceDbContext>(options => options.UseInMemoryDatabase(_databaseName));
            });
        }
    }

    /// <summary>Client của Admin nền tảng (role alias <c>adm</c> như trong token thật của AuthService).</summary>
    public HttpClient CreateAdminClient(string role = "adm") => CreateClientWithToken(role);

    /// <summary>Client của user thường, không có quyền quản lý catalog.</summary>
    public HttpClient CreateCustomerClient() => CreateClientWithToken("cus");

    public HttpClient CreateClientWithToken(string? role, string? tokenUse = null)
    {
        HttpClient client = CreateClient();
        string token = TestJwtHelper.GenerateToken(
            sub: Guid.NewGuid().ToString(),
            sid: Guid.NewGuid().ToString(),
            role: role,
            tokenUse: tokenUse);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task<Category> SeedCategoryAsync(string name = "Xe nâng", bool isActive = true)
    {
        Category category = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = "category-" + Guid.NewGuid().ToString("N"),
            IsActive = isActive
        };

        await WithDbContextAsync(async db =>
        {
            db.Categories.Add(category);
            await db.SaveChangesAsync();
        });

        return category;
    }

    public async Task WithDbContextAsync(Func<CatalogServiceDbContext, Task> action)
    {
        using IServiceScope scope = Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<CatalogServiceDbContext>());
    }
}
