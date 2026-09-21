using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Auth;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Errors;
using Dprental.AuthService.Domain.Exceptions;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.Models;
using Microsoft.AspNetCore.Identity;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class UserServiceTests
{
    [Fact]
    public async Task RegisterAsyncCreatesActiveUserWithNormalizedEmailAndHashedPassword()
    {
        DateTimeOffset now = new(2026, 9, 17, 9, 0, 0, TimeSpan.Zero);
        RecordingUserRepository repository = new();
        PasswordHasher<User> hasher = new();
        UserService service = CreateService(repository, hasher, new FixedTimeProvider(now));

        Result<AccountCreatedResponse> result = await service.RegisterAsync(
            new RegisterRequest("  Tan.Len@Example.COM ", "Matkhau123!", "  Huynh Tan Len "));

        Assert.True(result.IsSuccess);
        User created = Assert.Single(repository.CreatedUsers);
        Assert.Equal(result.Value.AccountId, created.Id);
        Assert.Equal("tan.len@example.com", created.Email);
        Assert.Equal("Huynh Tan Len", created.FullName);
        Assert.True(created.IsActive);
        Assert.Equal(now.UtcDateTime, created.CreatedAt);
        Assert.NotEqual("Matkhau123!", created.PasswordHash);
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(created, created.PasswordHash, "Matkhau123!"));
    }

    [Fact]
    public async Task RegisterAsyncExistingEmailReturnsConflictWithoutSaving()
    {
        RecordingUserRepository repository = new() { ExistingEmail = "tan.len@example.com" };
        UserService service = CreateService(repository, new PasswordHasher<User>(), TimeProvider.System);

        Result<AccountCreatedResponse> result = await service.RegisterAsync(
            new RegisterRequest("TAN.LEN@example.com", "Matkhau123!", "Huynh Tan Len"));

        Assert.True(result.IsFailure);
        Assert.Equal(UserErrors.EmailAlreadyExists, result.Error);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Empty(repository.CreatedUsers);
    }

    [Fact]
    public async Task RegisterAsyncConcurrentDuplicateRejectedByDatabaseReturnsConflict()
    {
        RecordingUserRepository repository = new() { RejectCreate = true };
        UserService service = CreateService(repository, new PasswordHasher<User>(), TimeProvider.System);

        Result<AccountCreatedResponse> result = await service.RegisterAsync(
            new RegisterRequest("tan.len@example.com", "Matkhau123!", "Huynh Tan Len"));

        Assert.True(result.IsFailure);
        Assert.Equal(UserErrors.EmailAlreadyExists, result.Error);
    }

    private static UserService CreateService(IUserRepository repository, IPasswordHasher<User> passwordHasher, TimeProvider timeProvider) =>
        new(repository, passwordHasher, timeProvider, new TestCorrelationIdProvider(), new TestAuditLogRepository(), new TestAuthSessionRepository());

    private sealed class TestCorrelationIdProvider : ICorrelationIdProvider
    {
        public string GetCorrelationId() => "test-correlation-id";
    }

    private sealed class TestAuditLogRepository : IAuditLogRepository
    {
        public Task AddAsync(AuditLog auditLog, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestAuthSessionRepository : IAuthSessionRepository
    {
        public Task RevokeAllSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AuthSessionPrincipal?> CreateAsync(Guid userId, string refreshTokenHash, DateTime utcNow, DateTime expiresAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AuthSessionPrincipal?> RotateAsync(string currentRefreshTokenHash, string replacementRefreshTokenHash, DateTime utcNow, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RevokeByRefreshTokenHashAsync(string refreshTokenHash, DateTime utcNow, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Session?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Session>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Session> CreateAsync(Session entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(Session entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingUserRepository : IUserRepository
    {
        public string? ExistingEmail { get; init; }
        public bool RejectCreate { get; init; }
        public List<User> CreatedUsers { get; } = [];

        public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
            Task.FromResult(normalizedEmail == ExistingEmail);

        public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("UserService.RegisterAsync does not authenticate users.");

        public Task<User> CreateAsync(User entity, CancellationToken cancellationToken = default)
        {
            if (RejectCreate)
            {
                throw new DuplicateEmailException();
            }

            CreatedUsers.Add(entity);
            return Task.FromResult(entity);
        }

        public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("UserService.RegisterAsync does not use generic CRUD reads.");

        public Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("UserService.RegisterAsync does not use generic CRUD reads.");

        public Task<bool> UpdateAsync(User entity, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("UserService.RegisterAsync does not update users.");

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("UserService.RegisterAsync does not delete users.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
