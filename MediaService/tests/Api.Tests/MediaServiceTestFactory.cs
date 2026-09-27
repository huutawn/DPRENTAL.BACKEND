using Dprental.MediaService.Api.Health;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Minio;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.MediaService.Api.Tests;

/// <summary>
/// WebApplicationFactory phục vụ kiểm thử tích hợp các endpoint và lifecycle của MediaService.
/// </summary>
public class MediaServiceTestFactory : WebApplicationFactory<Program>
{
    private static readonly RSA SigningKey = RSA.Create(2048);
    private const string Issuer = "https://auth.test";
    private const string Audience = "media-tests";
    private const string CatalogServiceId = "11111111-1111-1111-1111-111111111111";

    public static void SetUserToken(HttpClient client, string userId) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(userId));

    public static void SetServiceToken(HttpClient client, params string[] scopes) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            CreateToken(CatalogServiceId, true, scopes.Length == 0 ? ["media.verify"] : scopes));

    private static string CreateToken(string subject, bool isService = false, string[]? scopes = null)
    {
        var claims = new List<Claim> { new("sub", subject) };
        if (isService)
        {
            claims.Add(new Claim("u", "svc"));
            foreach (string scope in scopes ?? ["media.verify"])
            {
                claims.Add(new Claim("s", scope));
            }
        }

        var token = new JwtSecurityToken(
            Issuer,
            Audience,
            claims,
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: new SigningCredentials(new RsaSecurityKey(SigningKey), SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public IPostgreSqlHealthChecker? PostgreSqlHealthCheckerOverride { get; set; }
    public IMinioClient? MinioClientOverride { get; set; }
    public Dprental.MediaService.Application.IServices.IMediaUploadService? MediaUploadServiceOverride { get; set; }
    public Dprental.MediaService.Application.IServices.IMediaAccessService? MediaAccessServiceOverride { get; set; }
    public Dictionary<string, string?> ConfigurationOverrides { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var defaultSettings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=5434;Database=mediaservice;Username=mediaservice;Password=mediasecret",
                ["ObjectStorage:Endpoint"] = "http://127.0.0.1:9000",
                ["ObjectStorage:Bucket"] = "dprental-media",
                ["ObjectStorage:AccessKey"] = "mediaadmin",
                ["ObjectStorage:SecretKey"] = "mediasecretpassword",
                ["Jwt:Issuer"] = Issuer,
                ["Jwt:Audience"] = Audience,
                ["Jwt:JwksUri"] = "https://auth.test/.well-known/jwks.json",
                [$"Jwt:ServicePrincipals:{CatalogServiceId}"] = "CatalogService"
            };

            foreach (var (k, v) in ConfigurationOverrides)
            {
                defaultSettings[k] = v;
            }

            config.AddInMemoryCollection(defaultSettings);
        });

        builder.ConfigureServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.ConfigurationManager = null;
                options.TokenValidationParameters.IssuerSigningKey = new RsaSecurityKey(SigningKey);
            });
            if (PostgreSqlHealthCheckerOverride != null)
            {
                services.RemoveAll<IPostgreSqlHealthChecker>();
                services.AddSingleton(PostgreSqlHealthCheckerOverride);
            }

            if (MinioClientOverride != null)
            {
                services.RemoveAll<IMinioClient>();
                services.AddSingleton(MinioClientOverride);
            }

            if (MediaUploadServiceOverride != null)
            {
                services.RemoveAll<Dprental.MediaService.Application.IServices.IMediaUploadService>();
                services.AddSingleton(MediaUploadServiceOverride);
            }

            if (MediaAccessServiceOverride != null)
            {
                services.RemoveAll<Dprental.MediaService.Application.IServices.IMediaAccessService>();
                services.AddSingleton(MediaAccessServiceOverride);
            }
        });
    }
}
