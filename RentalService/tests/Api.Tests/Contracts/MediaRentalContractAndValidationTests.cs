using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Media;
using Dprental.RentalService.Application.Validation;
using Dprental.RentalService.Common;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Contracts;

public sealed class MediaRentalContractAndValidationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly DateTimeOffset _fixedTime = new(2026, 10, 8, 8, 0, 0, TimeSpan.FromHours(7));

    // 1. Route Specification Verification
    [Fact]
    public void MediaRoutes_MatchActualProviderEndpoints()
    {
        Guid mediaId = Guid.Parse("12345678-1234-1234-1234-123456789abc");

        Assert.Equal("/api/v1/media/12345678-1234-1234-1234-123456789abc/verify",
            MediaContractSpec.Routes.VerifyMedia(mediaId));

        Assert.Equal("/api/v1/media/12345678-1234-1234-1234-123456789abc",
            MediaContractSpec.Routes.GetMetadata(mediaId));

        Assert.Equal("/api/v1/media/12345678-1234-1234-1234-123456789abc/access",
            MediaContractSpec.Routes.CreatePrivateAccess(mediaId));
    }

    // 2. DTO Serialization Roundtrips
    [Fact]
    public void MediaVerifyDto_SerializesAndDeserializesCorrectly()
    {
        Guid mediaId = Guid.NewGuid();
        Guid uploaderId = Guid.NewGuid();

        MediaVerifyDto dto = new(
            MediaId: mediaId,
            UploadedByUserId: uploaderId.ToString("D"),
            Status: "Ready",
            OriginalFileName: "handover-proof.jpg",
            ContentType: "image/jpeg",
            SizeBytes: 2048576,
            CreatedAtUtc: _fixedTime);

        string json = JsonSerializer.Serialize(dto, JsonOptions);
        Assert.Contains("\"uploadedByUserId\":", json);
        Assert.Contains("\"status\":\"Ready\"", json);
        Assert.DoesNotContain("\"visibility\":", json); // Provider verify does NOT return visibility

        MediaVerifyDto? des = JsonSerializer.Deserialize<MediaVerifyDto>(json, JsonOptions);
        Assert.NotNull(des);
        Assert.Equal(mediaId, des.MediaId);
        Assert.Equal(uploaderId.ToString("D"), des.UploadedByUserId);
    }

    [Fact]
    public void MediaMetadataDto_SerializesAndDeserializesCorrectly()
    {
        Guid mediaId = Guid.NewGuid();
        Guid uploaderId = Guid.NewGuid();

        MediaMetadataDto dto = new(
            MediaId: mediaId,
            Visibility: "Private",
            Status: "Ready",
            CreatedAtUtc: _fixedTime);

        string json = JsonSerializer.Serialize(dto, JsonOptions);
        Assert.Contains("\"visibility\":\"Private\"", json);
        Assert.Contains("\"status\":\"Ready\"", json);

        MediaMetadataDto? des = JsonSerializer.Deserialize<MediaMetadataDto>(json, JsonOptions);
        Assert.NotNull(des);
        Assert.Equal("Private", des.Visibility);
    }

    [Fact]
    public void MediaAccess_RequestAndResponse_RoundtripSuccessfully()
    {
        Guid mediaId = Guid.NewGuid();
        CreateMediaAccessRequest req = new(Action: "read", Download: false);
        string reqJson = JsonSerializer.Serialize(req, JsonOptions);
        Assert.Contains("\"action\":\"read\"", reqJson);
        Assert.Contains("\"download\":false", reqJson);

        CreateMediaAccessRequest? reqDes = JsonSerializer.Deserialize<CreateMediaAccessRequest>(reqJson, JsonOptions);
        Assert.NotNull(reqDes);
        Assert.Equal("read", reqDes.Action);
        Assert.False(reqDes.Download);

        MediaAccessDto resp = new(mediaId, "https://storage.dprental.vn/private/signed?token=abc", _fixedTime.AddMinutes(15));
        string respJson = JsonSerializer.Serialize(resp, JsonOptions);
        MediaAccessDto? respDes = JsonSerializer.Deserialize<MediaAccessDto>(respJson, JsonOptions);
        Assert.NotNull(respDes);
        Assert.Equal("https://storage.dprental.vn/private/signed?token=abc", respDes.Url);
        Assert.Equal(_fixedTime.AddMinutes(15), respDes.ExpiresAtUtc);
    }

    // 3. Evidence Validation Rules (RentalEvidenceValidator)
    [Fact]
    public async Task ValidateEvidence_EmptyWhenOptional_ReturnsSuccess()
    {
        StubMediaClient stub = new();
        Result<IReadOnlyList<VerifiedEvidenceMediaDto>> result =
            await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync([], Guid.NewGuid(), stub, isRequired: false);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task ValidateEvidence_EmptyWhenRequired_Fails()
    {
        StubMediaClient stub = new();
        Result<IReadOnlyList<VerifiedEvidenceMediaDto>> result =
            await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync([], Guid.NewGuid(), stub, isRequired: true);

        Assert.True(result.IsFailure);
        Assert.Equal(MediaContractSpec.ErrorCodes.InvalidMediaId, result.Error.Code);
    }

    [Fact]
    public async Task ValidateEvidence_ExceedingTenItems_IsRejected()
    {
        StubMediaClient stub = new();
        List<Guid> elevenIds = Enumerable.Range(0, 11).Select(_ => Guid.NewGuid()).ToList();

        Result<IReadOnlyList<VerifiedEvidenceMediaDto>> result =
            await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync(elevenIds, Guid.NewGuid(), stub);

        Assert.True(result.IsFailure);
        Assert.Equal(MediaContractSpec.ErrorCodes.MediaLimitExceeded, result.Error.Code);
    }

    [Fact]
    public async Task ValidateEvidence_WithGuidEmpty_IsRejected()
    {
        StubMediaClient stub = new();
        List<Guid> ids = [Guid.NewGuid(), Guid.Empty];

        Result<IReadOnlyList<VerifiedEvidenceMediaDto>> result =
            await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync(ids, Guid.NewGuid(), stub);

        Assert.True(result.IsFailure);
        Assert.Equal(MediaContractSpec.ErrorCodes.InvalidMediaId, result.Error.Code);
    }

    [Fact]
    public async Task ValidateEvidence_WithDuplicateIds_IsRejected()
    {
        StubMediaClient stub = new();
        Guid duplicateId = Guid.NewGuid();
        List<Guid> ids = [duplicateId, Guid.NewGuid(), duplicateId];

        Result<IReadOnlyList<VerifiedEvidenceMediaDto>> result =
            await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync(ids, Guid.NewGuid(), stub);

        Assert.True(result.IsFailure);
        Assert.Equal(MediaContractSpec.ErrorCodes.DuplicateMediaId, result.Error.Code);
    }

    [Fact]
    public async Task ValidateEvidence_WhenMediaNotReady_IsRejected()
    {
        Guid mediaId = Guid.NewGuid();
        Guid uploaderId = Guid.NewGuid();

        StubMediaClient stub = new();
        stub.Items[mediaId] = new StubMediaRecord(mediaId, uploaderId, "Uploading", "Private", "f.jpg", "image/jpeg", 100, _fixedTime);

        Result<IReadOnlyList<VerifiedEvidenceMediaDto>> result =
            await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync([mediaId], uploaderId, stub);

        Assert.True(result.IsFailure);
        Assert.Equal(MediaContractSpec.ErrorCodes.MediaNotReady, result.Error.Code);
    }

    [Fact]
    public async Task ValidateEvidence_WhenMediaIsPublic_IsStrictlyRejected()
    {
        Guid mediaId = Guid.NewGuid();
        Guid uploaderId = Guid.NewGuid();

        StubMediaClient stub = new();
        // Public media must be rejected (cannot be used as evidence)
        stub.Items[mediaId] = new StubMediaRecord(mediaId, uploaderId, "Ready", "Public", "f.jpg", "image/jpeg", 100, _fixedTime);

        Result<IReadOnlyList<VerifiedEvidenceMediaDto>> result =
            await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync([mediaId], uploaderId, stub);

        Assert.True(result.IsFailure);
        Assert.Equal(MediaContractSpec.ErrorCodes.MediaMustBePrivate, result.Error.Code);
    }

    [Fact]
    public async Task ValidateEvidence_WhenUploaderMismatch_IsRejected()
    {
        Guid mediaId = Guid.NewGuid();
        Guid legitimateUploader = Guid.NewGuid();
        Guid impostorActor = Guid.NewGuid();

        StubMediaClient stub = new();
        stub.Items[mediaId] = new StubMediaRecord(mediaId, legitimateUploader, "Ready", "Private", "f.jpg", "image/jpeg", 100, _fixedTime);

        Result<IReadOnlyList<VerifiedEvidenceMediaDto>> result =
            await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync([mediaId], impostorActor, stub);

        Assert.True(result.IsFailure);
        Assert.Equal(MediaContractSpec.ErrorCodes.MediaUploaderMismatch, result.Error.Code);
    }

    [Fact]
    public async Task ValidateEvidence_WhenReturnedMediaIdDiffersFromRequested_IsRejected()
    {
        Guid mediaId = Guid.NewGuid();
        Guid uploaderId = Guid.NewGuid();
        Guid corruptedId = Guid.NewGuid();

        StubMediaClient stub = new();
        stub.Items[mediaId] = new StubMediaRecord(corruptedId, uploaderId, "Ready", "Private", "f.jpg", "image/jpeg", 100, _fixedTime);

        Result<IReadOnlyList<VerifiedEvidenceMediaDto>> result =
            await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync([mediaId], uploaderId, stub);

        Assert.True(result.IsFailure);
        Assert.Equal(MediaContractSpec.ErrorCodes.MediaIdMismatch, result.Error.Code);
    }

    [Fact]
    public async Task ValidateEvidence_AtomicRejection_IfAnyMediaInvalid()
    {
        Guid uploaderId = Guid.NewGuid();
        Guid valid1 = Guid.NewGuid();
        Guid public2 = Guid.NewGuid(); // Invalid: Public
        Guid valid3 = Guid.NewGuid();

        StubMediaClient stub = new();
        stub.Items[valid1] = new StubMediaRecord(valid1, uploaderId, "Ready", "Private", "1.jpg", "image/jpeg", 100, _fixedTime);
        stub.Items[public2] = new StubMediaRecord(public2, uploaderId, "Ready", "Public", "2.jpg", "image/jpeg", 100, _fixedTime);
        stub.Items[valid3] = new StubMediaRecord(valid3, uploaderId, "Ready", "Private", "3.jpg", "image/jpeg", 100, _fixedTime);

        Result<IReadOnlyList<VerifiedEvidenceMediaDto>> result =
            await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync([valid1, public2, valid3], uploaderId, stub);

        // Entire batch must fail atomically
        Assert.True(result.IsFailure);
        Assert.Equal(MediaContractSpec.ErrorCodes.MediaMustBePrivate, result.Error.Code);
    }

    [Fact]
    public async Task ValidateEvidence_AllValidPrivateMedia_ReturnsVerifiedList()
    {
        Guid uploaderId = Guid.NewGuid();
        Guid m1 = Guid.NewGuid();
        Guid m2 = Guid.NewGuid();

        StubMediaClient stub = new();
        stub.Items[m1] = new StubMediaRecord(m1, uploaderId, "Ready", "Private", "1.jpg", "image/jpeg", 100, _fixedTime);
        stub.Items[m2] = new StubMediaRecord(m2, uploaderId, "Ready", "Private", "2.jpg", "image/jpeg", 200, _fixedTime);

        Result<IReadOnlyList<VerifiedEvidenceMediaDto>> result =
            await RentalEvidenceValidator.ValidateAndVerifyEvidenceAsync([m1, m2], uploaderId, stub);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Count);
        Assert.Equal(m1, result.Value[0].MediaId);
        Assert.Equal(m2, result.Value[1].MediaId);
    }

    private sealed record StubMediaRecord(Guid MediaId, Guid UploaderUserId, string Status, string Visibility, string OriginalFileName, string ContentType, long SizeBytes, DateTimeOffset CreatedAtUtc);

    private sealed class StubMediaClient : IRentalMediaClient
    {
        public Dictionary<Guid, StubMediaRecord> Items { get; } = [];

        public Task<Result<MediaVerifyDto>> VerifyMediaAsync(Guid mediaId, CancellationToken ct = default)
        {
            if (Items.TryGetValue(mediaId, out var item))
            {
                return Task.FromResult(Result.Success(new MediaVerifyDto(item.MediaId, item.UploaderUserId.ToString("D"), item.Status, item.OriginalFileName, item.ContentType, item.SizeBytes, item.CreatedAtUtc)));
            }
            return Task.FromResult(Result.Failure<MediaVerifyDto>(
                Error.NotFound(MediaContractSpec.ErrorCodes.MediaNotFound, "Media not found.")));
        }

        public Task<Result<MediaMetadataDto>> GetMetadataAsync(Guid mediaId, CancellationToken ct = default)
        {
            if (Items.TryGetValue(mediaId, out var item))
            {
                return Task.FromResult(Result.Success(new MediaMetadataDto(item.MediaId, item.Visibility, item.Status, item.CreatedAtUtc)));
            }
            return Task.FromResult(Result.Failure<MediaMetadataDto>(
                Error.NotFound(MediaContractSpec.ErrorCodes.MediaNotFound, "Media not found.")));
        }

        public Task<Result<MediaAccessDto>> CreatePrivateReadAccessAsync(Guid mediaId, CreateMediaAccessRequest request, CancellationToken ct = default)
        {
            if (Items.TryGetValue(mediaId, out var item))
            {
                return Task.FromResult(Result.Success(new MediaAccessDto(
                    item.MediaId,
                    $"https://storage.dprental.vn/private/{item.MediaId}",
                    DateTimeOffset.UtcNow.AddMinutes(15))));
            }
            return Task.FromResult(Result.Failure<MediaAccessDto>(
                Error.NotFound(MediaContractSpec.ErrorCodes.MediaNotFound, "Media not found.")));
        }
    }
}
