using System.Diagnostics.CodeAnalysis;
using Dprental.AuthService.Application.Clients;
using Dprental.AuthService.Application.DTOs.Media;
using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Application.Options;
using Dprental.AuthService.Application.Validation;
using Dprental.AuthService.Application.Services;
using Dprental.AuthService.Application.Validators;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.DTOs.User;
using Dprental.AuthService.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test method naming convention")]
public sealed class MediaMigrationAcceptanceTests
{
    private static readonly Guid OwnerRoleId = new("a1010000-0000-0000-0000-000000000001");

    [Fact]
    public void StartupValidation_ValidatesAllRequiredSettings()
    {
        MediaServiceOptionsValidator validator = new();

        // 1. Missing InternalBaseUrl
        var opt1 = new MediaServiceOptions { InternalBaseUrl = "", PublicGatewayUrl = "http://gateway:5050", ApiKey = "key", TimeoutSeconds = 10 };
        Assert.True(validator.Validate(null, opt1).Failed);

        // 2. Invalid InternalBaseUrl
        var opt2 = new MediaServiceOptions { InternalBaseUrl = "not-a-url", PublicGatewayUrl = "http://gateway:5050", ApiKey = "key", TimeoutSeconds = 10 };
        Assert.True(validator.Validate(null, opt2).Failed);

        // 3. Missing PublicGatewayUrl
        var opt3 = new MediaServiceOptions { InternalBaseUrl = "http://media:8080", PublicGatewayUrl = "", ApiKey = "key", TimeoutSeconds = 10 };
        Assert.True(validator.Validate(null, opt3).Failed);

        // 4. Missing ApiKey
        var opt4 = new MediaServiceOptions { InternalBaseUrl = "http://media:8080", PublicGatewayUrl = "http://gateway:5050", ApiKey = "   ", TimeoutSeconds = 10 };
        Assert.True(validator.Validate(null, opt4).Failed);

        // 5. Invalid TimeoutSeconds
        var opt5 = new MediaServiceOptions { InternalBaseUrl = "http://media:8080", PublicGatewayUrl = "http://gateway:5050", ApiKey = "key", TimeoutSeconds = 0 };
        Assert.True(validator.Validate(null, opt5).Failed);

        // 6. Valid options succeed
        var valid = new MediaServiceOptions { InternalBaseUrl = "http://media-service:8080", PublicGatewayUrl = "http://localhost:5050", ApiKey = "valid-key", TimeoutSeconds = 15 };
        Assert.True(validator.Validate(null, valid).Succeeded);
    }

    [Fact]
    public void MediaVerificationRules_RejectsInvalidFilesAndAcceptsValidFiles()
    {
        Guid userId = Guid.NewGuid();
        Guid otherUser = Guid.NewGuid();
        Guid mediaId = Guid.NewGuid();

        // Not ready
        var notReady = new MediaVerificationDto(mediaId, "Uploading", userId.ToString(), "pic.png", "image/png", 1024, DateTimeOffset.UtcNow);
        var res1 = MediaVerificationRules.ValidateImage(notReady, userId);
        Assert.True(res1.IsFailure);
        Assert.Equal(AuthErrors.Media.NotReady, res1.Error);

        // Uploaded by another user
        var wrongOwner = new MediaVerificationDto(mediaId, "Ready", otherUser.ToString(), "pic.png", "image/png", 1024, DateTimeOffset.UtcNow);
        var res2 = MediaVerificationRules.ValidateImage(wrongOwner, userId);
        Assert.True(res2.IsFailure);
        Assert.Equal(AuthErrors.Media.Forbidden, res2.Error);

        // Wrong type for avatar (PDF instead of image)
        var wrongType = new MediaVerificationDto(mediaId, "Ready", userId.ToString(), "doc.pdf", "application/pdf", 1024, DateTimeOffset.UtcNow);
        var res3 = MediaVerificationRules.ValidateImage(wrongType, userId);
        Assert.True(res3.IsFailure);
        Assert.Equal(AuthErrors.Media.InvalidType, res3.Error);

        // Valid image
        var validImage = new MediaVerificationDto(mediaId, "Ready", userId.ToString(), "avatar.png", "image/png", 1024, DateTimeOffset.UtcNow);
        var res4 = MediaVerificationRules.ValidateImage(validImage, userId);
        Assert.True(res4.IsSuccess);

        // Valid document (PDF or image)
        var validDoc = new MediaVerificationDto(mediaId, "Ready", userId.ToString(), "license.pdf", "application/pdf", 1024, DateTimeOffset.UtcNow);
        var res5 = MediaVerificationRules.ValidateDocument(validDoc, userId);
        Assert.True(res5.IsSuccess);

        // Wrong type for document (mp4)
        var invalidDoc = new MediaVerificationDto(mediaId, "Ready", userId.ToString(), "video.mp4", "video/mp4", 1024, DateTimeOffset.UtcNow);
        var res6 = MediaVerificationRules.ValidateDocument(invalidDoc, userId);
        Assert.True(res6.IsFailure);
        Assert.Equal(AuthErrors.Media.InvalidType, res6.Error);
    }

    [Fact]
    public async Task UserUpdate_ResendingExistingMediaId_DoesNotReverifyOrRequeueOutbox()
    {
        Guid userId = Guid.NewGuid();
        Guid attachedMediaId = Guid.NewGuid();

        User user = new()
        {
            Id = userId,
            Email = "user@example.com",
            FullName = "User",
            AvatarMediaId = attachedMediaId,
            IsActive = true
        };

        UserAccountServiceTests.FakeUserRepository userRepo = new() { User = user };
        FakeMediaServiceClient mediaClient = new() { AutoVerify = false }; // AutoVerify is false; any verify call will fail!
        FakeMediaOutboxService outboxService = new();

        UserAccountService service = UserAccountServiceTests.CreateService(
            userRepo,
            mediaServiceClient: mediaClient,
            mediaOutboxService: outboxService);

        // Resend the already-attached mediaId
        UpdateProfileRequest request = new(AvatarMediaId: attachedMediaId);
        Result<CurrentUserResponse> result = await service.UpdateProfileAsync(userId, request);

        Assert.True(result.IsSuccess, $"{result.Error?.Code}: {result.Error?.Message}");
        Assert.Equal(attachedMediaId, result.Value.AvatarMediaId);
        Assert.Equal($"http://localhost:5050/api/v1/media/{attachedMediaId:D}/content", result.Value.AvatarUrl);
        Assert.Empty(outboxService.QueuedMediaIds); // No duplicate outbox task queued!
    }

    [Fact]
    public async Task UserUpdate_PutAndPatch_GoThroughSameFlowAndReturnBothMediaIdAndUrl()
    {
        Guid userId = Guid.NewGuid();
        Guid newMediaId = Guid.NewGuid();

        User user = new()
        {
            Id = userId,
            Email = "user@example.com",
            FullName = "Original Name",
            IsActive = true
        };

        UserAccountServiceTests.FakeUserRepository userRepo = new() { User = user };
        FakeMediaServiceClient mediaClient = new();
        mediaClient.Verifications[newMediaId] = new MediaVerificationDto(newMediaId, "Ready", userId.ToString(), "avatar.png", "image/png", 2048, DateTimeOffset.UtcNow);
        FakeMediaOutboxService outboxService = new();

        UserAccountService service = UserAccountServiceTests.CreateService(
            userRepo,
            mediaServiceClient: mediaClient,
            mediaOutboxService: outboxService);

        // Call via PUT DTO (UpdateUserProfileRequest)
        var putRequest = new UpdateUserProfileRequest("Updated Name", PhoneNumber: "0901234567", AvatarMediaId: newMediaId);
        Result<CurrentUserResponse> putResult = await service.UpdateUserProfileAsync(userId, putRequest);

        Assert.True(putResult.IsSuccess, $"{putResult.Error?.Code}: {putResult.Error?.Message}");
        Assert.Equal(newMediaId, putResult.Value.AvatarMediaId);
        Assert.Equal($"http://localhost:5050/api/v1/media/{newMediaId:D}/content", putResult.Value.AvatarUrl);
        Assert.Equal("Updated Name", putResult.Value.FullName);
        Assert.Contains(newMediaId, outboxService.QueuedMediaIds);

        // Call via PATCH DTO (UpdateProfileRequest) with removeAvatar: true
        var patchRequest = new UpdateProfileRequest(RemoveAvatar: true);
        Result<CurrentUserResponse> patchResult = await service.UpdateProfileAsync(userId, patchRequest);

        Assert.True(patchResult.IsSuccess, $"{patchResult.Error?.Code}: {patchResult.Error?.Message}");
        Assert.Null(patchResult.Value.AvatarMediaId);
        Assert.Null(patchResult.Value.AvatarUrl);
        Assert.Null(user.AvatarMediaId);
    }

    [Fact]
    public async Task Organization_GetById_PermissionCheck_And_LicenseExpiration()
    {
        Guid ownerId = Guid.NewGuid();
        Guid outsiderId = Guid.NewGuid();
        Guid orgId = Guid.NewGuid();
        Guid logoMediaId = Guid.NewGuid();
        Guid licenseMediaId = Guid.NewGuid();

        OrganizationServiceTests.FakeOrganizationRepository orgRepo = new();
        OrganizationServiceTests.FakeMembershipRepository memRepo = new();
        OrganizationServiceTests.FakeRoleRepository roleRepo = new();

        Organization org = new()
        {
            Id = orgId,
            Name = "Alpha Corp",
            Slug = "alpha-corp",
            Status = OrganizationStatus.Active,
            LogoMediaId = logoMediaId,
            BusinessLicenseDocumentMediaId = licenseMediaId
        };
        orgRepo.Organizations[orgId] = org;

        Role ownerRole = new() { Id = OwnerRoleId, Name = SystemRoleCode.Owner, Scope = RoleScope.Organization };
        roleRepo.Roles[OwnerRoleId] = ownerRole;

        // Owner membership
        memRepo.Memberships.Add(new OrganizationMembership
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            UserId = ownerId,
            RoleId = OwnerRoleId,
            Status = MembershipStatus.Active,
            Role = ownerRole
        });

        FakeMediaUrlResolver urlResolver = new();
        OrganizationService service = new(
            orgRepo,
            memRepo,
            roleRepo,
            FixedCorrelationIdProvider.Instance,
            urlResolver,
            NullLogger<OrganizationService>.Instance);

        // 1. Outsider gets 403 CrossOrganizationAccessDenied
        var outsiderResult = await service.GetByIdAsync(outsiderId, orgId);
        Assert.True(outsiderResult.IsFailure);
        Assert.Equal(AuthErrors.Organization.CrossOrganizationAccessDenied, outsiderResult.Error);

        // 2. Owner gets 200 with logo and signed license URL with expiration
        var ownerResult = await service.GetByIdAsync(ownerId, orgId);
        Assert.True(ownerResult.IsSuccess, $"{ownerResult.Error?.Code}: {ownerResult.Error?.Message}");
        Assert.Equal(logoMediaId, ownerResult.Value.LogoMediaId);
        Assert.Equal($"http://localhost:5050/api/v1/media/{logoMediaId:D}/content", ownerResult.Value.LogoUrl);
        Assert.Equal(licenseMediaId, ownerResult.Value.BusinessLicenseDocumentMediaId);
        Assert.NotNull(ownerResult.Value.BusinessLicenseDocumentUrl);
        Assert.NotNull(ownerResult.Value.BusinessLicenseDocumentUrlExpiresAtUtc);

        // 3. MediaService error when issuing signed license URL returns ServiceUnavailable and does not leak internal data
        urlResolver.AccessError = AuthErrors.Media.ServiceUnavailable;
        var failedResult = await service.GetByIdAsync(ownerId, orgId);
        Assert.True(failedResult.IsFailure);
        Assert.Equal(AuthErrors.Media.ServiceUnavailable, failedResult.Error);
    }

    [Fact]
    public async Task Outbox_Processing_RetriesAndStoresErrors_AndIsIdempotent()
    {
        Guid mediaId = Guid.NewGuid();
        FakeMediaOutboxRepository outboxRepo = new();
        FakeMediaServiceClient mediaClient = new();

        MediaOutboxService outboxService = new(
            outboxRepo,
            mediaClient,
            TimeProvider.System,
            NullLogger<MediaOutboxService>.Instance);

        // 1. Queue task
        await outboxService.QueuePublicVisibilityAsync(mediaId);
        var msg = Assert.Single(outboxRepo.Messages);
        Assert.Equal("Pending", msg.Status);
        Assert.Equal(mediaId, msg.MediaId);

        // 2. Simulate MediaService failure
        mediaClient.VisibilityError = AuthErrors.Media.ServiceUnavailable;
        int processed1 = await outboxService.ProcessPendingAsync();
        Assert.Equal(0, processed1);
        Assert.Equal("Pending", msg.Status);
        Assert.Equal(1, msg.RetryCount);
        Assert.NotNull(msg.LastError);
        Assert.NotNull(msg.NextRetryAtUtc);

        // 3. Simulate recovery and successful processing
        mediaClient.VisibilityError = null;
        Assert.Equal(0, await outboxService.ProcessPendingAsync()); // Backoff is enforced.
        msg.NextRetryAtUtc = DateTime.UtcNow.AddSeconds(-1);
        int processed2 = await outboxService.ProcessPendingAsync();
        Assert.Equal(1, processed2);
        Assert.Equal("Completed", msg.Status);
        Assert.NotNull(msg.ProcessedAtUtc);
        Assert.Null(msg.LastError);

        // 4. Idempotency: running again processes 0 items
        int processed3 = await outboxService.ProcessPendingAsync();
        Assert.Equal(0, processed3);

        // Verify media service received the visibility update (1 failure + 1 success = 2 calls)
        Assert.Equal(2, mediaClient.VisibilityCalls.Count);
        var lastCall = mediaClient.VisibilityCalls.Last();
        Assert.Equal(mediaId, lastCall.MediaId);
        Assert.Equal("public", lastCall.Visibility);
    }
}
