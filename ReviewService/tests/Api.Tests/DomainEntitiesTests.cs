using Dprental.ReviewService.Common.Enums;
using Dprental.ReviewService.Domain.Entities;
using Xunit;

namespace Dprental.ReviewService.Api.Tests;

public sealed class DomainEntitiesTests
{
    [Fact]
    public void Review_InitializesPropertiesCorrectly()
    {
        var id = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var equipmentListingId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var canEditUntil = now.AddDays(7);

        var review = new Review(
            id: id,
            orderId: orderId,
            itemId: itemId,
            direction: ReviewDirection.renter_to_supplier_listing,
            authorId: authorId,
            subjectId: subjectId,
            equipmentListingId: equipmentListingId,
            rating: 5,
            comment: "Excellent camera gear",
            authorState: AuthorState.Active,
            moderationState: ModerationState.Visible,
            version: 1,
            createdAt: now,
            updatedAt: null,
            withdrawnAt: null,
            adminHiddenAt: null,
            adminReason: null,
            canEditUntil: canEditUntil);

        Assert.Equal(id, review.Id);
        Assert.Equal(orderId, review.OrderId);
        Assert.Equal(itemId, review.ItemId);
        Assert.Equal(ReviewDirection.renter_to_supplier_listing, review.Direction);
        Assert.Equal(authorId, review.AuthorId);
        Assert.Equal(subjectId, review.SubjectId);
        Assert.Equal(equipmentListingId, review.EquipmentListingId);
        Assert.Equal(5, review.Rating);
        Assert.Equal("Excellent camera gear", review.Comment);
        Assert.Equal(AuthorState.Active, review.AuthorState);
        Assert.Equal(ModerationState.Visible, review.ModerationState);
        Assert.Equal(1L, review.Version);
        Assert.Equal(now, review.CreatedAt);
        Assert.Null(review.UpdatedAt);
        Assert.Null(review.WithdrawnAt);
        Assert.Null(review.AdminHiddenAt);
        Assert.Null(review.AdminReason);
        Assert.Equal(canEditUntil, review.CanEditUntil);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(10)]
    public void Review_RatingInvariant_ThrowsOutOfRange_WhenOutside1To5(int invalidRating)
    {
        var authorId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentOutOfRangeException>(() => new Review(
            id: Guid.NewGuid(),
            orderId: Guid.NewGuid(),
            itemId: Guid.NewGuid(),
            direction: ReviewDirection.renter_to_supplier_listing,
            authorId: authorId,
            subjectId: subjectId,
            equipmentListingId: Guid.NewGuid(),
            rating: invalidRating,
            comment: null,
            authorState: AuthorState.Active,
            moderationState: ModerationState.Visible,
            version: 1,
            createdAt: now,
            updatedAt: null,
            withdrawnAt: null,
            adminHiddenAt: null,
            adminReason: null,
            canEditUntil: now.AddDays(7)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Review_RatingInvariant_AcceptsValidRatings(int validRating)
    {
        var authorId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var review = new Review(
            id: Guid.NewGuid(),
            orderId: Guid.NewGuid(),
            itemId: Guid.NewGuid(),
            direction: ReviewDirection.renter_to_supplier_listing,
            authorId: authorId,
            subjectId: subjectId,
            equipmentListingId: Guid.NewGuid(),
            rating: validRating,
            comment: null,
            authorState: AuthorState.Active,
            moderationState: ModerationState.Visible,
            version: 1,
            createdAt: now,
            updatedAt: null,
            withdrawnAt: null,
            adminHiddenAt: null,
            adminReason: null,
            canEditUntil: now.AddDays(7));

        Assert.Equal(validRating, review.Rating);
    }

    [Fact]
    public void Review_AuthorCannotReviewSelfInvariant_ThrowsArgumentException()
    {
        var sameUserId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentException>(() => new Review(
            id: Guid.NewGuid(),
            orderId: Guid.NewGuid(),
            itemId: Guid.NewGuid(),
            direction: ReviewDirection.renter_to_supplier_listing,
            authorId: sameUserId,
            subjectId: sameUserId,
            equipmentListingId: Guid.NewGuid(),
            rating: 5,
            comment: null,
            authorState: AuthorState.Active,
            moderationState: ModerationState.Visible,
            version: 1,
            createdAt: now,
            updatedAt: null,
            withdrawnAt: null,
            adminHiddenAt: null,
            adminReason: null,
            canEditUntil: now.AddDays(7)));
    }

    [Fact]
    public void Review_PropertySetters_EnforceInvariants()
    {
        var authorId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var review = Review.Create(
            id: Guid.NewGuid(),
            orderId: Guid.NewGuid(),
            itemId: Guid.NewGuid(),
            direction: ReviewDirection.renter_to_supplier_listing,
            authorId: authorId,
            subjectId: subjectId,
            equipmentListingId: Guid.NewGuid(),
            rating: 4,
            comment: "Good",
            createdAt: now,
            canEditUntil: now.AddDays(7));

        Assert.Throws<ArgumentOutOfRangeException>(() => review.Rating = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => review.Rating = 6);
        Assert.Throws<ArgumentException>(() => review.AuthorId = subjectId);
        Assert.Throws<ArgumentException>(() => review.SubjectId = authorId);

        review.Rating = 5;
        Assert.Equal(5, review.Rating);
    }

    [Fact]
    public void Review_LifecycleMethods_UpdateStateAndIncrementVersion()
    {
        var authorId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var review = Review.Create(
            id: Guid.NewGuid(),
            orderId: Guid.NewGuid(),
            itemId: Guid.NewGuid(),
            direction: ReviewDirection.renter_to_supplier_listing,
            authorId: authorId,
            subjectId: subjectId,
            equipmentListingId: Guid.NewGuid(),
            rating: 4,
            comment: "Original comment",
            createdAt: now,
            canEditUntil: now.AddDays(7));

        Assert.Equal(1L, review.Version);

        // Update
        var updateTime = now.AddHours(2);
        review.Update(5, "Updated comment", updateTime);
        Assert.Equal(5, review.Rating);
        Assert.Equal("Updated comment", review.Comment);
        Assert.Equal(updateTime, review.UpdatedAt);
        Assert.Equal(2L, review.Version);

        // Withdraw
        var withdrawTime = now.AddDays(1);
        review.Withdraw(withdrawTime);
        Assert.Equal(AuthorState.Withdrawn, review.AuthorState);
        Assert.Equal(withdrawTime, review.WithdrawnAt);
        Assert.Equal(3L, review.Version);

        // AdminHide
        var hideTime = now.AddDays(2);
        review.AdminHide("Inappropriate content", hideTime);
        Assert.Equal(ModerationState.Hidden, review.ModerationState);
        Assert.Equal("Inappropriate content", review.AdminReason);
        Assert.Equal(hideTime, review.AdminHiddenAt);
        Assert.Equal(4L, review.Version);

        // AdminRestore
        review.AdminRestore();
        Assert.Equal(ModerationState.Visible, review.ModerationState);
        Assert.Null(review.AdminReason);
        Assert.Null(review.AdminHiddenAt);
        Assert.Equal(5L, review.Version);
    }

    [Fact]
    public void ReviewAudit_InitializesPropertiesCorrectly()
    {
        var auditId = Guid.NewGuid();
        var reviewId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var audit = new ReviewAudit(
            id: auditId,
            reviewId: reviewId,
            actorId: actorId,
            actorRole: "Renter",
            action: ReviewAuditAction.Created,
            reason: "Initial submission",
            oldValues: null,
            newValues: "{\"rating\": 5}",
            createdAt: now);

        Assert.Equal(auditId, audit.Id);
        Assert.Equal(reviewId, audit.ReviewId);
        Assert.Equal(actorId, audit.ActorId);
        Assert.Equal("Renter", audit.ActorRole);
        Assert.Equal(ReviewAuditAction.Created, audit.Action);
        Assert.Equal("Initial submission", audit.Reason);
        Assert.Null(audit.OldValues);
        Assert.Equal("{\"rating\": 5}", audit.NewValues);
        Assert.Equal(now, audit.CreatedAt);
    }

    [Fact]
    public void IdempotencyRecord_InitializesPropertiesAndAliasesCorrectly()
    {
        var id = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddDays(30);

        var record = new IdempotencyRecord(
            id: id,
            actorId: actorId,
            idempotencyKey: "idem-key-123",
            requestFingerprint: "sha256-fingerprint",
            httpMethod: "POST",
            requestPath: "/api/v1/reviews",
            responseStatusCode: 201,
            responseHeaders: "{\"ETag\":\"\\\"1\\\"\"}",
            responseBody: "{\"reviewId\":\"abc\"}",
            createdAt: now,
            expiresAt: expiresAt);

        Assert.Equal(id, record.Id);
        Assert.Equal(actorId, record.ActorId);
        Assert.Equal("idem-key-123", record.IdempotencyKey);
        Assert.Equal("sha256-fingerprint", record.RequestFingerprint);
        Assert.Equal("POST", record.HttpMethod);
        Assert.Equal("/api/v1/reviews", record.RequestPath);
        Assert.Equal(201, record.ResponseStatusCode);
        Assert.Equal("{\"ETag\":\"\\\"1\\\"\"}", record.ResponseHeaders);
        Assert.Equal("{\"reviewId\":\"abc\"}", record.ResponseBody);
        Assert.Equal(now, record.CreatedAt);
        Assert.Equal(expiresAt, record.ExpiresAt);

        // Test aliases
        Assert.Equal("idem-key-123", record.Key);
        Assert.Equal("sha256-fingerprint", record.RequestHash);
        Assert.Equal(201, record.ResponseStatus);

        record.Key = "new-key";
        Assert.Equal("new-key", record.IdempotencyKey);
        record.RequestHash = "new-hash";
        Assert.Equal("new-hash", record.RequestFingerprint);
        record.ResponseStatus = 200;
        Assert.Equal(200, record.ResponseStatusCode);
    }
}
