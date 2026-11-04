using System.Globalization;
using System.Text.Json;
using Dprental.ReviewService.Application.Clients;
using Dprental.ReviewService.Application.DTOs.Reviews;
using Dprental.ReviewService.Common;
using Dprental.ReviewService.Common.Enums;
using Dprental.ReviewService.Common.Utils;
using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Dprental.ReviewService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dprental.ReviewService.Application.Services;

/// <summary>
/// Triển khai nghiệp vụ ứng dụng cho ReviewService (IReviewService).
/// Điều phối quy trình tạo, cập nhật, rút và truy vấn chi tiết đánh giá với kiểm tra tính toàn vẹn,
/// idempotency, concurrency token (ETag) và giao dịch nguyên tử.
/// </summary>
public sealed class ReviewService : IReviewService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ReviewServiceDbContext _dbContext;
    private readonly IReviewRepository _reviewRepository;
    private readonly IIdempotencyService _idempotencyService;
    private readonly IRentalServiceClient _rentalServiceClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReviewService>? _logger;

    public ReviewService(
        ReviewServiceDbContext dbContext,
        IReviewRepository reviewRepository,
        IIdempotencyService idempotencyService,
        IRentalServiceClient rentalServiceClient,
        TimeProvider? timeProvider = null,
        ILogger<ReviewService>? logger = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _reviewRepository = reviewRepository ?? throw new ArgumentNullException(nameof(reviewRepository));
        _idempotencyService = idempotencyService ?? throw new ArgumentNullException(nameof(idempotencyService));
        _rentalServiceClient = rentalServiceClient ?? throw new ArgumentNullException(nameof(rentalServiceClient));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public ReviewService(
        ReviewServiceDbContext dbContext,
        IIdempotencyService idempotencyService,
        IRentalServiceClient rentalServiceClient,
        TimeProvider? timeProvider = null,
        ILogger<ReviewService>? logger = null)
        : this(dbContext, new ReviewRepository(dbContext), idempotencyService, rentalServiceClient, timeProvider, logger)
    {
    }

    /// <inheritdoc />
    public async Task<Result<ReviewDetailResponse>> CreateReviewAsync(
        Guid actorId,
        CreateReviewRequest request,
        string idempotencyKey,
        string? userType = null,
        CancellationToken cancellationToken = default)
    {
        var actorError = ValidateActor(actorId, userType);
        if (actorError != null)
        {
            return Result.Failure<ReviewDetailResponse>(actorError);
        }

        if (request == null)
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.Validation("INVALID_REQUEST", "Dữ liệu yêu cầu là bắt buộc."));
        }

        if (request.OrderId == Guid.Empty || request.ItemId == Guid.Empty)
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.Validation("INVALID_REQUEST", "OrderId và ItemId là bắt buộc và không được rỗng."));
        }

        if (request.Rating < 1 || request.Rating > 5)
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.Validation("INVALID_RATING", "Điểm đánh giá phải từ 1 đến 5 sao."));
        }

        if (!UnicodeRuneHelper.IsValidCommentLength(request.Comment))
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.Validation("INVALID_COMMENT_LENGTH", "Bình luận không được vượt quá 1.000 ký tự Unicode."));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.Validation("IDEMPOTENCY_KEY_REQUIRED", "Khóa Idempotency-Key là bắt buộc đối với yêu cầu này."));
        }

        // 1. Kiểm tra Idempotency
        string requestPath = "/api/v1/reviews";
        string requestBody = JsonSerializer.Serialize(request, JsonOptions);
        var checkResult = await _idempotencyService.CheckAsync(
            actorId,
            idempotencyKey,
            "POST",
            requestPath,
            requestBody,
            null,
            cancellationToken);

        if (checkResult.IsFailure)
        {
            return Result.Failure<ReviewDetailResponse>(checkResult.Error);
        }

        if (checkResult.Value != null)
        {
            var cachedDto = JsonSerializer.Deserialize<ReviewDetailResponse>(checkResult.Value.ResponseBody, JsonOptions);
            if (cachedDto != null)
            {
                _logger?.LogInformation("Trả về kết quả tạo review đã lưu (Replay) cho key {Key}", idempotencyKey);
                return Result.Success(cachedDto);
            }
        }

        // 2. Kiểm tra trùng lặp trước khi gọi RentalService
        bool exists = await _reviewRepository.ExistsByItemAndDirectionAsync(
            request.ItemId,
            ReviewDirection.renter_to_supplier_listing,
            cancellationToken);

        if (exists)
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.Conflict("REVIEW_ALREADY_EXISTS", "Mục thuê này đã được bạn đánh giá trước đó. Mỗi bên chỉ được phép gửi đánh giá một lần cho mỗi mục thuê."));
        }

        // 3. Gọi snapshot từ RentalService
        var snapshotResult = await _rentalServiceClient.GetReviewSnapshotAsync(
            request.OrderId,
            request.ItemId,
            cancellationToken);

        if (snapshotResult.IsFailure)
        {
            return Result.Failure<ReviewDetailResponse>(snapshotResult.Error);
        }

        var snapshot = snapshotResult.Value;

        // 4. Xác thực trạng thái mục thuê hoàn tất
        if (!string.Equals(snapshot.Status, "Completed", StringComparison.OrdinalIgnoreCase) || snapshot.CompletedAt == null)
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.Unprocessable("RENTAL_ITEM_NOT_COMPLETED", "Mục thuê chưa hoàn tất toàn bộ quy trình hoàn trả và quyết toán."));
        }

        // 5. Xác thực quyền tham gia của actor (direction = renter_to_supplier_listing)
        if (actorId != snapshot.RenterId)
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.Forbidden("ACTOR_NOT_PARTICIPANT", "Bạn không có quyền đánh giá mục thuê này do không phải là Người thuê hoặc Nhà cung cấp trong đơn hàng."));
        }

        // 6. Xác thực cửa sổ 30 ngày (exclusive UTC)
        var now = _timeProvider.GetUtcNow();
        var completedAt = snapshot.CompletedAt.Value;
        var creationDeadline = completedAt.AddDays(30);

        if (now >= creationDeadline)
        {
            string formattedDeadline = creationDeadline.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            return Result.Failure<ReviewDetailResponse>(
                Error.Unprocessable(
                    "REVIEW_CREATION_WINDOW_EXPIRED",
                    $"Thời hạn tạo đánh giá cho mục thuê này đã kết thúc vào lúc {formattedDeadline} (quy tắc completedAt + 30 ngày)."));
        }

        // 7. Tính canEditUntil = min(now + 7 days, completedAt + 30 days)
        var sevenDaysLater = now.AddDays(7);
        var canEditUntil = sevenDaysLater < creationDeadline ? sevenDaysLater : creationDeadline;

        // 8. Khởi tạo thực thể Review
        var review = Review.Create(
            id: Guid.NewGuid(),
            orderId: request.OrderId,
            itemId: request.ItemId,
            direction: ReviewDirection.renter_to_supplier_listing,
            authorId: actorId,
            subjectId: snapshot.SupplierId,
            equipmentListingId: snapshot.EquipmentListingId,
            rating: request.Rating,
            comment: request.Comment,
            createdAt: now,
            canEditUntil: canEditUntil);

        // 9. Khởi tạo bản ghi ReviewAudit (action Created)
        var audit = new ReviewAudit(
            id: Guid.NewGuid(),
            reviewId: review.Id,
            actorId: actorId,
            actorRole: "Renter",
            action: ReviewAuditAction.Created,
            reason: "Initial review submission",
            oldValues: null,
            newValues: JsonSerializer.Serialize(new
            {
                rating = review.Rating,
                comment = review.Comment,
                authorState = review.AuthorState.ToString(),
                moderationState = review.ModerationState.ToString(),
                version = review.Version
            }, JsonOptions),
            createdAt: now);

        // 10. Khởi tạo IdempotencyRecord
        var responseDto = ReviewDetailResponse.FromEntity(review);
        string responseBody = JsonSerializer.Serialize(responseDto, JsonOptions);
        string fingerprint = _idempotencyService.ComputeFingerprint(actorId, "POST", requestPath, requestBody, null);

        var idempotencyRecord = _idempotencyService.CreateRecord(
            actorId: actorId,
            idempotencyKey: idempotencyKey,
            requestFingerprint: fingerprint,
            httpMethod: "POST",
            requestPath: requestPath,
            responseStatusCode: 201,
            responseHeaders: JsonSerializer.Serialize(new
            {
                ETag = $"\"{review.Version}\"",
                Location = $"/api/v1/reviews/{review.Id}"
            }),
            responseBody: responseBody,
            createdAt: now);

        // 11. Cam kết giao dịch nguyên tử (Review + ReviewAudit + IdempotencyRecord)
        try
        {
            if (_dbContext.Database.IsRelational())
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                await _dbContext.Reviews.AddAsync(review, cancellationToken);
                await _dbContext.ReviewAudits.AddAsync(audit, cancellationToken);
                await _dbContext.IdempotencyRecords.AddAsync(idempotencyRecord, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await _dbContext.Reviews.AddAsync(review, cancellationToken);
                await _dbContext.ReviewAudits.AddAsync(audit, cancellationToken);
                await _dbContext.IdempotencyRecords.AddAsync(idempotencyRecord, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        catch (DbUpdateException ex)
        {
            _logger?.LogWarning(ex, "Xung đột ghi cơ sở dữ liệu khi tạo review cho mục thuê {ItemId}", request.ItemId);
            return Result.Failure<ReviewDetailResponse>(
                Error.Conflict("REVIEW_ALREADY_EXISTS", "Mục thuê này đã được bạn đánh giá trước đó. Mỗi bên chỉ được phép gửi đánh giá một lần cho mỗi mục thuê."));
        }

        return Result.Success(responseDto);
    }

    /// <inheritdoc />
    public Task<Result<ReviewDetailResponse>> CreateReviewAsync(
        string? actorId,
        CreateReviewRequest request,
        string idempotencyKey,
        string? userType = null,
        CancellationToken cancellationToken = default)
    {
        if (!ParseActorId(actorId, userType, out var parsedActorId, out var error))
        {
            return Task.FromResult(Result.Failure<ReviewDetailResponse>(error!));
        }

        return CreateReviewAsync(parsedActorId, request, idempotencyKey, userType, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<ReviewDetailResponse>> CreateReviewAsync(
        ReviewActorContext actor,
        CreateReviewRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return CreateReviewAsync(actor.ActorId, request, idempotencyKey, actor.UserType, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<ReviewDetailResponse>> UpdateReviewAsync(
        Guid actorId,
        Guid reviewId,
        UpdateReviewRequest request,
        string idempotencyKey,
        string? ifMatch,
        string? userType = null,
        CancellationToken cancellationToken = default)
    {
        var actorError = ValidateActor(actorId, userType);
        if (actorError != null)
        {
            return Result.Failure<ReviewDetailResponse>(actorError);
        }

        if (request == null)
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.Validation("INVALID_REQUEST", "Dữ liệu yêu cầu là bắt buộc."));
        }

        if (request.Rating < 1 || request.Rating > 5)
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.Validation("INVALID_RATING", "Điểm đánh giá phải từ 1 đến 5 sao."));
        }

        if (!UnicodeRuneHelper.IsValidCommentLength(request.Comment))
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.Validation("INVALID_COMMENT_LENGTH", "Bình luận không được vượt quá 1.000 ký tự Unicode."));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.Validation("IDEMPOTENCY_KEY_REQUIRED", "Khóa Idempotency-Key là bắt buộc đối với yêu cầu này."));
        }

        // 1. Kiểm tra Idempotency
        string requestPath = $"/api/v1/reviews/{reviewId}";
        string requestBody = JsonSerializer.Serialize(request, JsonOptions);
        var checkResult = await _idempotencyService.CheckAsync(
            actorId,
            idempotencyKey,
            "PUT",
            requestPath,
            requestBody,
            ifMatch,
            cancellationToken);

        if (checkResult.IsFailure)
        {
            return Result.Failure<ReviewDetailResponse>(checkResult.Error);
        }

        if (checkResult.Value != null)
        {
            var cachedDto = JsonSerializer.Deserialize<ReviewDetailResponse>(checkResult.Value.ResponseBody, JsonOptions);
            if (cachedDto != null)
            {
                _logger?.LogInformation("Trả về kết quả chỉnh sửa review đã lưu (Replay) cho key {Key}", idempotencyKey);
                return Result.Success(cachedDto);
            }
        }

        // 2. Tìm review hiện tại
        var review = await _reviewRepository.GetByIdAsync(reviewId, cancellationToken);
        if (review == null)
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.NotFound("REVIEW_NOT_FOUND", "Không tìm thấy đánh giá đã chỉ định."));
        }

        // 3. Kiểm tra If-Match so với review.Version
        if (!ParseIfMatch(ifMatch, review.Version, out var ifMatchError))
        {
            return Result.Failure<ReviewDetailResponse>(ifMatchError!);
        }

        // 4. Kiểm tra quyền sở hữu tác giả
        if (actorId != review.AuthorId)
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.Forbidden("FORBIDDEN_NOT_AUTHOR", "Chỉ chính tác giả mới có quyền chỉnh sửa đánh giá này."));
        }

        // 5. Kiểm tra AuthorState == Active
        if (review.AuthorState != AuthorState.Active)
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.Unprocessable("REVIEW_ALREADY_WITHDRAWN", "Đánh giá đã được rút lại bởi tác giả, không thể chỉnh sửa."));
        }

        // 6. Kiểm tra hạn chỉnh sửa: now < review.CanEditUntil
        var now = _timeProvider.GetUtcNow();
        if (now >= review.CanEditUntil)
        {
            string formattedDeadline = review.CanEditUntil.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            return Result.Failure<ReviewDetailResponse>(
                Error.Unprocessable(
                    "REVIEW_EDIT_WINDOW_EXPIRED",
                    $"Thời hạn chỉnh sửa đánh giá đã hết hạn vào lúc {formattedDeadline} (quy tắc min(createdAt + 7 ngày, completedAt + 30 ngày))."));
        }

        // 7. Cập nhật review, ghi log ReviewAudit và lưu IdempotencyRecord
        var oldValues = JsonSerializer.Serialize(new
        {
            rating = review.Rating,
            comment = review.Comment,
            version = review.Version
        }, JsonOptions);

        review.Update(request.Rating, request.Comment, now);

        var audit = new ReviewAudit(
            id: Guid.NewGuid(),
            reviewId: review.Id,
            actorId: actorId,
            actorRole: "Author",
            action: ReviewAuditAction.Updated,
            reason: "Author updated review",
            oldValues: oldValues,
            newValues: JsonSerializer.Serialize(new
            {
                rating = review.Rating,
                comment = review.Comment,
                version = review.Version
            }, JsonOptions),
            createdAt: now);

        var responseDto = ReviewDetailResponse.FromEntity(review);
        string responseBody = JsonSerializer.Serialize(responseDto, JsonOptions);
        string fingerprint = _idempotencyService.ComputeFingerprint(actorId, "PUT", requestPath, requestBody, ifMatch);

        var idempotencyRecord = _idempotencyService.CreateRecord(
            actorId: actorId,
            idempotencyKey: idempotencyKey,
            requestFingerprint: fingerprint,
            httpMethod: "PUT",
            requestPath: requestPath,
            responseStatusCode: 200,
            responseHeaders: JsonSerializer.Serialize(new { ETag = $"\"{review.Version}\"" }),
            responseBody: responseBody,
            createdAt: now);

        // 8. Commit giao dịch nguyên tử
        if (_dbContext.Database.IsRelational())
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            AttachIfDetached(review);
            await _dbContext.ReviewAudits.AddAsync(audit, cancellationToken);
            await _dbContext.IdempotencyRecords.AddAsync(idempotencyRecord, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            AttachIfDetached(review);
            await _dbContext.ReviewAudits.AddAsync(audit, cancellationToken);
            await _dbContext.IdempotencyRecords.AddAsync(idempotencyRecord, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return Result.Success(responseDto);
    }

    /// <inheritdoc />
    public Task<Result<ReviewDetailResponse>> UpdateReviewAsync(
        string? actorId,
        Guid reviewId,
        UpdateReviewRequest request,
        string idempotencyKey,
        string? ifMatch,
        string? userType = null,
        CancellationToken cancellationToken = default)
    {
        if (!ParseActorId(actorId, userType, out var parsedActorId, out var error))
        {
            return Task.FromResult(Result.Failure<ReviewDetailResponse>(error!));
        }

        return UpdateReviewAsync(parsedActorId, reviewId, request, idempotencyKey, ifMatch, userType, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<ReviewDetailResponse>> UpdateReviewAsync(
        ReviewActorContext actor,
        Guid reviewId,
        UpdateReviewRequest request,
        string idempotencyKey,
        string? ifMatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return UpdateReviewAsync(actor.ActorId, reviewId, request, idempotencyKey, ifMatch, actor.UserType, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<ReviewDetailResponse>> WithdrawReviewAsync(
        Guid actorId,
        Guid reviewId,
        WithdrawReviewRequest? request,
        string idempotencyKey,
        string? ifMatch,
        string? userType = null,
        CancellationToken cancellationToken = default)
    {
        var actorError = ValidateActor(actorId, userType);
        if (actorError != null)
        {
            return Result.Failure<ReviewDetailResponse>(actorError);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.Validation("IDEMPOTENCY_KEY_REQUIRED", "Khóa Idempotency-Key là bắt buộc đối với yêu cầu này."));
        }

        // 1. Kiểm tra Idempotency
        string requestPath = $"/api/v1/reviews/{reviewId}/withdraw";
        string requestBody = request != null ? JsonSerializer.Serialize(request, JsonOptions) : string.Empty;
        var checkResult = await _idempotencyService.CheckAsync(
            actorId,
            idempotencyKey,
            "POST",
            requestPath,
            requestBody,
            ifMatch,
            cancellationToken);

        if (checkResult.IsFailure)
        {
            return Result.Failure<ReviewDetailResponse>(checkResult.Error);
        }

        if (checkResult.Value != null)
        {
            var cachedDto = JsonSerializer.Deserialize<ReviewDetailResponse>(checkResult.Value.ResponseBody, JsonOptions);
            if (cachedDto != null)
            {
                _logger?.LogInformation("Trả về kết quả rút review đã lưu (Replay) cho key {Key}", idempotencyKey);
                return Result.Success(cachedDto);
            }
        }

        // 2. Tìm review hiện tại
        var review = await _reviewRepository.GetByIdAsync(reviewId, cancellationToken);
        if (review == null)
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.NotFound("REVIEW_NOT_FOUND", "Không tìm thấy đánh giá đã chỉ định."));
        }

        // 3. Kiểm tra If-Match so với review.Version
        if (!ParseIfMatch(ifMatch, review.Version, out var ifMatchError))
        {
            return Result.Failure<ReviewDetailResponse>(ifMatchError!);
        }

        // 4. Kiểm tra quyền sở hữu tác giả
        if (actorId != review.AuthorId)
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.Forbidden("FORBIDDEN_NOT_AUTHOR", "Chỉ chính tác giả mới có quyền rút đánh giá này."));
        }

        // 5. Kiểm tra AuthorState chưa bị rút trước đó
        if (review.AuthorState == AuthorState.Withdrawn)
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.Conflict("REVIEW_ALREADY_WITHDRAWN", "Đánh giá này đã được rút lại trước đó."));
        }

        // 6. Chuyển authorState sang Withdrawn, ghi nhận withdrawnAt, tăng version
        var now = _timeProvider.GetUtcNow();
        var oldValues = JsonSerializer.Serialize(new
        {
            authorState = review.AuthorState.ToString(),
            version = review.Version
        }, JsonOptions);

        review.Withdraw(now);

        var audit = new ReviewAudit(
            id: Guid.NewGuid(),
            reviewId: review.Id,
            actorId: actorId,
            actorRole: "Author",
            action: ReviewAuditAction.Withdrawn,
            reason: request?.Reason ?? "Author withdrew review",
            oldValues: oldValues,
            newValues: JsonSerializer.Serialize(new
            {
                authorState = review.AuthorState.ToString(),
                withdrawnAt = review.WithdrawnAt,
                version = review.Version
            }, JsonOptions),
            createdAt: now);

        var responseDto = ReviewDetailResponse.FromEntity(review);
        string responseBody = JsonSerializer.Serialize(responseDto, JsonOptions);
        string fingerprint = _idempotencyService.ComputeFingerprint(actorId, "POST", requestPath, requestBody, ifMatch);

        var idempotencyRecord = _idempotencyService.CreateRecord(
            actorId: actorId,
            idempotencyKey: idempotencyKey,
            requestFingerprint: fingerprint,
            httpMethod: "POST",
            requestPath: requestPath,
            responseStatusCode: 200,
            responseHeaders: JsonSerializer.Serialize(new { ETag = $"\"{review.Version}\"" }),
            responseBody: responseBody,
            createdAt: now);

        // 7. Commit giao dịch nguyên tử (bảo toàn ràng buộc item_id, direction)
        if (_dbContext.Database.IsRelational())
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            AttachIfDetached(review);
            await _dbContext.ReviewAudits.AddAsync(audit, cancellationToken);
            await _dbContext.IdempotencyRecords.AddAsync(idempotencyRecord, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            AttachIfDetached(review);
            await _dbContext.ReviewAudits.AddAsync(audit, cancellationToken);
            await _dbContext.IdempotencyRecords.AddAsync(idempotencyRecord, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return Result.Success(responseDto);
    }

    /// <inheritdoc />
    public Task<Result<ReviewDetailResponse>> WithdrawReviewAsync(
        string? actorId,
        Guid reviewId,
        WithdrawReviewRequest? request,
        string idempotencyKey,
        string? ifMatch,
        string? userType = null,
        CancellationToken cancellationToken = default)
    {
        if (!ParseActorId(actorId, userType, out var parsedActorId, out var error))
        {
            return Task.FromResult(Result.Failure<ReviewDetailResponse>(error!));
        }

        return WithdrawReviewAsync(parsedActorId, reviewId, request, idempotencyKey, ifMatch, userType, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<ReviewDetailResponse>> WithdrawReviewAsync(
        ReviewActorContext actor,
        Guid reviewId,
        WithdrawReviewRequest? request,
        string idempotencyKey,
        string? ifMatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return WithdrawReviewAsync(actor.ActorId, reviewId, request, idempotencyKey, ifMatch, actor.UserType, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<ReviewDetailResponse>> GetReviewDetailAsync(
        Guid reviewId,
        Guid actorId,
        bool isAdmin = false,
        CancellationToken cancellationToken = default)
    {
        if (actorId == Guid.Empty)
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.Unauthorized("UNAUTHORIZED", "Người dùng chưa được xác thực."));
        }

        var review = await _reviewRepository.GetByIdAsync(reviewId, cancellationToken);
        if (review == null)
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.NotFound("REVIEW_NOT_FOUND", "Không tìm thấy đánh giá đã chỉ định."));
        }

        // Quyền riêng tư: Chỉ Author, Subject, hoặc Admin mới được xem
        bool isAuthor = actorId == review.AuthorId;
        bool isSubject = actorId == review.SubjectId;

        if (!isAuthor && !isSubject && !isAdmin)
        {
            // Trả về 404 REVIEW_NOT_FOUND để không tiết lộ sự tồn tại của review
            return Result.Failure<ReviewDetailResponse>(
                Error.NotFound("REVIEW_NOT_FOUND", "Không tìm thấy đánh giá đã chỉ định."));
        }

        return Result.Success(ReviewDetailResponse.FromEntity(review));
    }

    /// <inheritdoc />
    public Task<Result<ReviewDetailResponse>> GetReviewDetailAsync(
        Guid reviewId,
        string? actorId,
        bool isAdmin = false,
        CancellationToken cancellationToken = default)
    {
        if (!ParseActorId(actorId, null, out var parsedActorId, out var error))
        {
            return Task.FromResult(Result.Failure<ReviewDetailResponse>(error!));
        }

        return GetReviewDetailAsync(reviewId, parsedActorId, isAdmin, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<ReviewDetailResponse>> GetReviewDetailAsync(
        Guid reviewId,
        ReviewActorContext actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return GetReviewDetailAsync(reviewId, actor.ActorId, actor.IsAdmin, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<PublicReviewItemResponse>>> GetListingReviewsAsync(
        Guid listingId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (listingId == Guid.Empty)
        {
            return Result.Failure<PagedResult<PublicReviewItemResponse>>(
                Error.Validation("INVALID_LISTING_ID", "Mã thiết bị không hợp lệ."));
        }

        var safePage = Math.Max(1, page);
        var safePageSize = pageSize < 1 ? 20 : Math.Min(pageSize, 50);

        var (items, totalCount) = await _reviewRepository.GetPublicListingReviewsAsync(
            listingId,
            safePage,
            safePageSize,
            cancellationToken);

        var mappedItems = items
            .Select(r => PublicReviewItemResponse.FromEntity(r))
            .ToList();

        var totalPages = totalCount <= 0 ? 0 : (int)Math.Ceiling((double)totalCount / safePageSize);

        return Result.Success(new PagedResult<PublicReviewItemResponse>(
            mappedItems,
            safePage,
            safePageSize,
            totalCount,
            totalPages));
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<PublicReviewItemResponse>>> GetSupplierReviewsAsync(
        Guid supplierId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (supplierId == Guid.Empty)
        {
            return Result.Failure<PagedResult<PublicReviewItemResponse>>(
                Error.Validation("INVALID_SUPPLIER_ID", "Mã nhà cung cấp không hợp lệ."));
        }

        var safePage = Math.Max(1, page);
        var safePageSize = pageSize < 1 ? 20 : Math.Min(pageSize, 50);

        var (items, totalCount) = await _reviewRepository.GetPublicSupplierReviewsAsync(
            supplierId,
            safePage,
            safePageSize,
            cancellationToken);

        var mappedItems = items
            .Select(r => PublicReviewItemResponse.FromEntity(r))
            .ToList();

        var totalPages = totalCount <= 0 ? 0 : (int)Math.Ceiling((double)totalCount / safePageSize);

        return Result.Success(new PagedResult<PublicReviewItemResponse>(
            mappedItems,
            safePage,
            safePageSize,
            totalCount,
            totalPages));
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<ReviewDetailResponse>>> GetMyReviewsAsync(
        Guid actorId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (actorId == Guid.Empty)
        {
            return Result.Failure<PagedResult<ReviewDetailResponse>>(
                Error.Unauthorized("UNAUTHORIZED", "Mã người dùng không hợp lệ."));
        }

        var safePage = Math.Max(1, page);
        var safePageSize = pageSize < 1 ? 20 : Math.Min(pageSize, 50);

        var (items, totalCount) = await _reviewRepository.GetMyReviewsAsync(
            actorId,
            safePage,
            safePageSize,
            cancellationToken);

        var mappedItems = items
            .Select(ReviewDetailResponse.FromEntity)
            .ToList();

        var totalPages = totalCount <= 0 ? 0 : (int)Math.Ceiling((double)totalCount / safePageSize);

        return Result.Success(new PagedResult<ReviewDetailResponse>(
            mappedItems,
            safePage,
            safePageSize,
            totalCount,
            totalPages));
    }

    /// <inheritdoc />
    public Task<Result<PagedResult<ReviewDetailResponse>>> GetMyReviewsAsync(
        string? actorId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (!ParseActorId(actorId, null, out var parsedActorId, out var error))
        {
            return Task.FromResult(Result.Failure<PagedResult<ReviewDetailResponse>>(error!));
        }

        return GetMyReviewsAsync(parsedActorId, page, pageSize, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<PagedResult<ReviewDetailResponse>>> GetMyReviewsAsync(
        ReviewActorContext actor,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return GetMyReviewsAsync(actor.ActorId, page, pageSize, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<ReviewSummaryResponse>> GetSummaryAsync(
        string targetType,
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        var isListing = string.Equals(targetType, "listing", StringComparison.OrdinalIgnoreCase);
        var isSupplier = string.Equals(targetType, "supplier", StringComparison.OrdinalIgnoreCase);

        if (!isListing && !isSupplier)
        {
            return Result.Failure<ReviewSummaryResponse>(
                Error.Validation(
                    "INVALID_TARGET_TYPE",
                    $"Loại đối tượng không hợp lệ: '{targetType}'. Chỉ chấp nhận 'listing' hoặc 'supplier'."));
        }

        if (targetId == Guid.Empty)
        {
            return Result.Failure<ReviewSummaryResponse>(
                Error.Validation("INVALID_TARGET_ID", "Mã đối tượng không hợp lệ."));
        }

        var normalizedTargetType = isListing ? "listing" : "supplier";

        var (totalCount, averageRating, distribution) = await _reviewRepository.GetRatingSummaryAsync(
            normalizedTargetType,
            targetId,
            cancellationToken);

        var stringDistribution = new Dictionary<string, int>
        {
            ["5"] = distribution != null && distribution.TryGetValue(5, out var c5) ? c5 : 0,
            ["4"] = distribution != null && distribution.TryGetValue(4, out var c4) ? c4 : 0,
            ["3"] = distribution != null && distribution.TryGetValue(3, out var c3) ? c3 : 0,
            ["2"] = distribution != null && distribution.TryGetValue(2, out var c2) ? c2 : 0,
            ["1"] = distribution != null && distribution.TryGetValue(1, out var c1) ? c1 : 0
        };

        var response = new ReviewSummaryResponse(
            normalizedTargetType,
            targetId,
            totalCount == 0 ? null : averageRating,
            totalCount,
            stringDistribution);

        return Result.Success(response);
    }

    private static Error? ValidateActor(Guid actorId, string? userType)
    {
        if (string.Equals(userType, "svc", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Forbidden("FORBIDDEN_SERVICE_TOKEN", "Service token không được phép thực hiện thao tác đánh giá.");
        }

        if (actorId == Guid.Empty)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Mã người dùng không hợp lệ.");
        }

        return null;
    }

    private static bool ParseActorId(
        string? actorId,
        string? userType,
        out Guid parsedActorId,
        out Error? error)
    {
        if (string.Equals(userType, "svc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(actorId, "svc", StringComparison.OrdinalIgnoreCase))
        {
            parsedActorId = Guid.Empty;
            error = Error.Forbidden("FORBIDDEN_SERVICE_TOKEN", "Service token không được phép thực hiện thao tác đánh giá.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(actorId) ||
            !Guid.TryParse(actorId, out parsedActorId) ||
            parsedActorId == Guid.Empty)
        {
            parsedActorId = Guid.Empty;
            error = Error.Unauthorized("UNAUTHORIZED", "Mã người dùng không phải là UUID hợp lệ.");
            return false;
        }

        error = null;
        return true;
    }

    private static bool ParseIfMatch(
        string? ifMatch,
        long currentVersion,
        out Error? error)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            error = Error.PreconditionRequired(
                "PRECONDITION_REQUIRED",
                "Yêu cầu chỉnh sửa hoặc thay đổi trạng thái đánh giá bắt buộc phải có header 'If-Match' chứa ETag phiên bản hiện tại.");
            return false;
        }

        var clean = ifMatch.Trim();
        if (clean.StartsWith('"') && clean.EndsWith('"') && clean.Length >= 2)
        {
            clean = clean[1..^1];
        }

        if (clean == "*" || !long.TryParse(clean, CultureInfo.InvariantCulture, out long version) || version != currentVersion)
        {
            error = Error.PreconditionFailed(
                "ETAG_PRECONDITION_FAILED",
                "Đánh giá đã bị thay đổi bởi thao tác khác (phiên bản gửi lên không khớp với cơ sở dữ liệu). Vui lòng tải lại trước khi thao tác.");
            return false;
        }

        error = null;
        return true;
    }

    private void AttachIfDetached(Review review)
    {
        var entry = _dbContext.Entry(review);
        if (entry.State == EntityState.Detached)
        {
            _dbContext.Reviews.Attach(review);
            entry.State = EntityState.Modified;
        }
    }
}
