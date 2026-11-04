using System.Globalization;
using System.Text.Json;
using Dprental.ReviewService.Application.Kafka.Events;
using Dprental.ReviewService.Application.Clients;
using Dprental.ReviewService.Application.DTOs.Reviews;
using Dprental.ReviewService.Common;
using Dprental.ReviewService.Common.Enums;
using Dprental.ReviewService.Common.Utils;
using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dprental.ReviewService.Application.Services;

/// <summary>
/// Dịch vụ quản lý tác giả đánh giá (Create, Update, Withdraw) với giao dịch nguyên tử qua IUnitOfWork.
/// </summary>
public sealed class ReviewAuthoringService : IReviewAuthoringService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IUnitOfWork _unitOfWork;
    private readonly IReviewRepository _reviewRepository;
    private readonly IReviewAuditRepository _reviewAuditRepository;
    private readonly IIdempotencyRepository _idempotencyRepository;
    private readonly IIdempotencyService _idempotencyService;
    private readonly IRentalServiceClient _rentalServiceClient;
    private readonly IOutboxRepository _outboxRepository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReviewAuthoringService>? _logger;

    public ReviewAuthoringService(
        IUnitOfWork unitOfWork,
        IReviewRepository reviewRepository,
        IReviewAuditRepository reviewAuditRepository,
        IIdempotencyRepository idempotencyRepository,
        IIdempotencyService idempotencyService,
        IRentalServiceClient rentalServiceClient,
        IOutboxRepository outboxRepository,
        TimeProvider? timeProvider = null,
        ILogger<ReviewAuthoringService>? logger = null)
    {
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _reviewRepository = reviewRepository ?? throw new ArgumentNullException(nameof(reviewRepository));
        _reviewAuditRepository = reviewAuditRepository ?? throw new ArgumentNullException(nameof(reviewAuditRepository));
        _idempotencyRepository = idempotencyRepository ?? throw new ArgumentNullException(nameof(idempotencyRepository));
        _idempotencyService = idempotencyService ?? throw new ArgumentNullException(nameof(idempotencyService));
        _rentalServiceClient = rentalServiceClient ?? throw new ArgumentNullException(nameof(rentalServiceClient));
        _outboxRepository = outboxRepository ?? throw new ArgumentNullException(nameof(outboxRepository));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<ReviewDetailResponse>> CreateReviewAsync(
        ReviewActorContext actor,
        CreateReviewRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var actorError = ValidateActor(actor.ActorId, actor.UserType);
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
            actor.ActorId,
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

        // 2. Gọi snapshot từ RentalService ngoài transaction
        var snapshotResult = await _rentalServiceClient.GetReviewSnapshotAsync(
            request.OrderId,
            request.ItemId,
            cancellationToken);

        if (snapshotResult.IsFailure)
        {
            return Result.Failure<ReviewDetailResponse>(snapshotResult.Error);
        }

        var snapshot = snapshotResult.Value;

        // 3. Xác thực trạng thái mục thuê hoàn tất
        if (!string.Equals(snapshot.Status, "Completed", StringComparison.OrdinalIgnoreCase) || snapshot.CompletedAt == null)
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.Unprocessable("RENTAL_ITEM_NOT_COMPLETED", "Mục thuê chưa hoàn tất toàn bộ quy trình hoàn trả và quyết toán."));
        }

        // 4. Xác thực vai trò và xác định direction từ snapshot
        ReviewDirection direction;
        Guid subjectId;
        string actorRole;
        if (actor.ActorId == snapshot.RenterId)
        {
            if (actor.ActorId == snapshot.SupplierId)
            {
                return Result.Failure<ReviewDetailResponse>(
                    Error.Forbidden("ACTOR_NOT_PARTICIPANT", "Người thuê và Nhà cung cấp không được là cùng một tài khoản."));
            }
            direction = ReviewDirection.renter_to_supplier_listing;
            subjectId = snapshot.SupplierId;
            actorRole = "Renter";
        }
        else if (actor.ActorId == snapshot.SupplierId)
        {
            direction = ReviewDirection.supplier_to_renter;
            subjectId = snapshot.RenterId;
            actorRole = "Supplier";
        }
        else
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.Forbidden("ACTOR_NOT_PARTICIPANT", "Bạn không có quyền đánh giá mục thuê này do không phải là Người thuê hoặc Nhà cung cấp trong đơn hàng."));
        }

        // 5. Xác thực cửa sổ 30 ngày (exclusive UTC)
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

        // 6. Tính canEditUntil = min(now + 7 days, completedAt + 30 days)
        var sevenDaysLater = now.AddDays(7);
        var canEditUntil = sevenDaysLater < creationDeadline ? sevenDaysLater : creationDeadline;

        // 7. Mở transaction và lấy PostgreSQL transaction advisory lock theo cặp (actorId, idempotencyKey)
        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            await _idempotencyRepository.AcquireLockAsync(actor.ActorId, idempotencyKey, cancellationToken);

            // 8. Đọc lại receipt sau khi lấy khóa
            string fingerprint = _idempotencyService.ComputeFingerprint(actor.ActorId, "POST", requestPath, requestBody, null);
            var lockCheck = await _idempotencyService.CheckAsync(
                actor.ActorId,
                idempotencyKey,
                fingerprint,
                cancellationToken);

            if (lockCheck.IsFailure)
            {
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return Result.Failure<ReviewDetailResponse>(lockCheck.Error);
            }

            if (lockCheck.Value != null)
            {
                var cachedDto = JsonSerializer.Deserialize<ReviewDetailResponse>(lockCheck.Value.ResponseBody, JsonOptions);
                if (cachedDto != null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    _logger?.LogInformation("Trả về kết quả tạo review đã lưu (Replay under lock) cho key {Key}", idempotencyKey);
                    return Result.Success(cachedDto);
                }
            }

            // 9. Đọc trạng thái cần sửa (ExistsByItemAndDirectionAsync) dưới khóa
            bool exists = await _reviewRepository.ExistsByItemAndDirectionAsync(
                request.ItemId,
                direction,
                cancellationToken);

            if (exists)
            {
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return Result.Failure<ReviewDetailResponse>(
                    Error.Conflict("REVIEW_ALREADY_EXISTS", "Mục thuê này đã được bạn đánh giá trước đó. Mỗi bên chỉ được phép gửi đánh giá một lần cho mỗi mục thuê."));
            }

            // 10. Khởi tạo thực thể Review
            var review = Review.Create(
                id: Guid.NewGuid(),
                orderId: request.OrderId,
                itemId: request.ItemId,
                direction: direction,
                authorId: actor.ActorId,
                subjectId: subjectId,
                equipmentListingId: snapshot.EquipmentListingId,
                rating: request.Rating,
                comment: request.Comment,
                createdAt: now,
                canEditUntil: canEditUntil);

            // 11. Khởi tạo bản ghi ReviewAudit (action Created)
            var audit = new ReviewAudit(
                id: Guid.NewGuid(),
                reviewId: review.Id,
                actorId: actor.ActorId,
                actorRole: actorRole,
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

            // 12. Khởi tạo IdempotencyRecord
            var responseDto = ReviewDetailResponse.FromEntity(review);
            string responseBody = JsonSerializer.Serialize(responseDto, JsonOptions);

            var idempotencyRecord = _idempotencyService.CreateRecord(
                actorId: actor.ActorId,
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

            // 13. Chuẩn bị Outbox events
            var publicOutbox = new OutboxMessage(
                Guid.NewGuid(),
                ReviewEventFactory.PublicEventsTopic,
                "ReviewCreated",
                review.ListingId.ToString("D"),
                JsonSerializer.Serialize(ReviewEventFactory.CreateContributionEvent(review), JsonOptions),
                now);

            var privateOutbox = new OutboxMessage(
                Guid.NewGuid(),
                "review.private.events.v1",
                "ReviewCreated",
                review.Id.ToString("D"),
                JsonSerializer.Serialize(new
                {
                    reviewId = review.Id,
                    reviewVersion = review.Version,
                    direction = review.Direction.ToString(),
                    authorId = review.AuthorId,
                    recipientId = review.SubjectId,
                    orderId = review.OrderId,
                    itemId = review.ItemId,
                    eventType = "ReviewCreated"
                }, JsonOptions),
                now);

            await _reviewRepository.AddAsync(review, cancellationToken);
            await _reviewAuditRepository.AddAsync(audit, cancellationToken);
            await _outboxRepository.AddAsync(publicOutbox, cancellationToken);
            await _outboxRepository.AddAsync(privateOutbox, cancellationToken);
            await _idempotencyRepository.SaveAsync(idempotencyRecord, cancellationToken);

            await _unitOfWork.CommitAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return Result.Success(responseDto);
        }
        catch (DbUpdateException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            _unitOfWork.DiscardChanges();

            if (IsReviewUniqueConstraintViolation(ex))
            {
                _logger?.LogWarning(ex, "Xung đột ghi cơ sở dữ liệu khi tạo review cho mục thuê {ItemId}", request.ItemId);
                return Result.Failure<ReviewDetailResponse>(
                    Error.Conflict("REVIEW_ALREADY_EXISTS", "Mục thuê này đã được bạn đánh giá trước đó. Mỗi bên chỉ được phép gửi đánh giá một lần cho mỗi mục thuê."));
            }

            string fingerprint = _idempotencyService.ComputeFingerprint(actor.ActorId, "POST", requestPath, requestBody, null);
            if (IsIdempotencyUniqueConstraintViolation(ex))
            {
                var recheck = await _idempotencyService.CheckAsync(actor.ActorId, idempotencyKey, fingerprint, cancellationToken);
                if (recheck.IsSuccess && recheck.Value != null)
                {
                    var cached = JsonSerializer.Deserialize<ReviewDetailResponse>(recheck.Value.ResponseBody, JsonOptions);
                    if (cached != null) return Result.Success(cached);
                }
                return Result.Failure<ReviewDetailResponse>(recheck.Error ?? Error.Conflict("IDEMPOTENCY_KEY_REUSE_MISMATCH", "Khóa Idempotency-Key đã được sử dụng trước đó cho một yêu cầu có nội dung khác."));
            }

            _logger?.LogError(ex, "Lỗi cơ sở dữ liệu khi tạo review cho mục thuê {ItemId}", request.ItemId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Result<ReviewDetailResponse>> UpdateReviewAsync(
        ReviewActorContext actor,
        Guid reviewId,
        UpdateReviewRequest request,
        string idempotencyKey,
        string? ifMatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var actorError = ValidateActor(actor.ActorId, actor.UserType);
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
        string fingerprint = _idempotencyService.ComputeFingerprint(actor.ActorId, "PUT", requestPath, requestBody, ifMatch);
        var checkResult = await _idempotencyService.CheckAsync(
            actor.ActorId,
            idempotencyKey,
            fingerprint,
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

        // 2. Mở transaction và lấy PostgreSQL transaction advisory lock theo cặp (actorId, idempotencyKey)
        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            await _idempotencyRepository.AcquireLockAsync(actor.ActorId, idempotencyKey, cancellationToken);

            // 3. Đọc lại receipt sau khi lấy khóa (Replay check TRƯỚC ETag check)
            var lockCheck = await _idempotencyService.CheckAsync(
                actor.ActorId,
                idempotencyKey,
                fingerprint,
                cancellationToken);

            if (lockCheck.IsFailure)
            {
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return Result.Failure<ReviewDetailResponse>(lockCheck.Error);
            }

            if (lockCheck.Value != null)
            {
                var cachedDto = JsonSerializer.Deserialize<ReviewDetailResponse>(lockCheck.Value.ResponseBody, JsonOptions);
                if (cachedDto != null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    _logger?.LogInformation("Trả về kết quả chỉnh sửa review đã lưu (Replay under lock) cho key {Key}", idempotencyKey);
                    return Result.Success(cachedDto);
                }
            }

            // 4. Tìm review hiện tại
            var review = await _reviewRepository.GetByIdAsync(reviewId, cancellationToken);
            if (review == null)
            {
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return Result.Failure<ReviewDetailResponse>(
                    Error.NotFound("REVIEW_NOT_FOUND", "Không tìm thấy đánh giá đã chỉ định."));
            }

            // 5. Kiểm tra If-Match so với review.Version
            if (!ParseIfMatch(ifMatch, review.Version, out var ifMatchError))
            {
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return Result.Failure<ReviewDetailResponse>(ifMatchError!);
            }

            // 6. Kiểm tra quyền sở hữu tác giả
            if (actor.ActorId != review.AuthorId)
            {
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return Result.Failure<ReviewDetailResponse>(
                    Error.NotFound("REVIEW_NOT_FOUND", "Không tìm thấy đánh giá hoặc bạn không có quyền chỉnh sửa."));
            }

            // 7. Kiểm tra trạng thái AuthorState (chỉ Active mới được sửa)
            if (review.AuthorState != AuthorState.Active)
            {
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return Result.Failure<ReviewDetailResponse>(
                    Error.Unprocessable("REVIEW_ALREADY_WITHDRAWN", "Không thể chỉnh sửa đánh giá đã được rút lại."));
            }

            // 8. Kiểm tra thời hạn canEditUntil
            var now = _timeProvider.GetUtcNow();
            if (now >= review.CanEditUntil)
            {
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                string formattedCanEditUntil = review.CanEditUntil.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                return Result.Failure<ReviewDetailResponse>(
                    Error.Unprocessable(
                        "REVIEW_EDIT_WINDOW_EXPIRED",
                        $"Thời hạn chỉnh sửa đánh giá này đã kết thúc vào lúc {formattedCanEditUntil}."));
            }

            // 9. Lưu giá trị cũ trước khi sửa
            string oldValuesJson = JsonSerializer.Serialize(new
            {
                rating = review.Rating,
                comment = review.Comment,
                version = review.Version
            }, JsonOptions);

            // 10. Cập nhật thực thể
            review.Update(request.Rating, request.Comment, now);

            // 11. Ghi audit
            string newValuesJson = JsonSerializer.Serialize(new
            {
                rating = review.Rating,
                comment = review.Comment,
                version = review.Version
            }, JsonOptions);

            var audit = new ReviewAudit(
                id: Guid.NewGuid(),
                reviewId: review.Id,
                actorId: actor.ActorId,
                actorRole: review.Direction == ReviewDirection.renter_to_supplier_listing ? "Renter" : "Supplier",
                action: ReviewAuditAction.Updated,
                reason: "Author updated rating or comment",
                oldValues: oldValuesJson,
                newValues: newValuesJson,
                createdAt: now);

            // 12. Tạo IdempotencyRecord
            var responseDto = ReviewDetailResponse.FromEntity(review);
            string responseBody = JsonSerializer.Serialize(responseDto, JsonOptions);

            var idempotencyRecord = _idempotencyService.CreateRecord(
                actorId: actor.ActorId,
                idempotencyKey: idempotencyKey,
                requestFingerprint: fingerprint,
                httpMethod: "PUT",
                requestPath: requestPath,
                responseStatusCode: 200,
                responseHeaders: JsonSerializer.Serialize(new { ETag = $"\"{review.Version}\"" }),
                responseBody: responseBody,
                createdAt: now);

            var updateOutbox = new OutboxMessage(
                Guid.NewGuid(),
                ReviewEventFactory.PublicEventsTopic,
                "ReviewUpdated",
                review.ListingId.ToString("D"),
                JsonSerializer.Serialize(ReviewEventFactory.CreateContributionEvent(review), JsonOptions),
                now);

            await _reviewRepository.UpdateAsync(review, cancellationToken);
            await _reviewAuditRepository.AddAsync(audit, cancellationToken);
            await _outboxRepository.AddAsync(updateOutbox, cancellationToken);
            await _idempotencyRepository.SaveAsync(idempotencyRecord, cancellationToken);

            await _unitOfWork.CommitAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return Result.Success(responseDto);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            _unitOfWork.DiscardChanges();
            return Result.Failure<ReviewDetailResponse>(
                Error.PreconditionFailed("ETAG_PRECONDITION_FAILED", "Phiên bản của đánh giá đã bị thay đổi bởi thao tác khác."));
        }
        catch (DbUpdateException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            _unitOfWork.DiscardChanges();

            if (IsIdempotencyUniqueConstraintViolation(ex))
            {
                var recheck = await _idempotencyService.CheckAsync(actor.ActorId, idempotencyKey, fingerprint, cancellationToken);
                if (recheck.IsSuccess && recheck.Value != null)
                {
                    var cached = JsonSerializer.Deserialize<ReviewDetailResponse>(recheck.Value.ResponseBody, JsonOptions);
                    if (cached != null) return Result.Success(cached);
                }
                return Result.Failure<ReviewDetailResponse>(recheck.Error ?? Error.Conflict("IDEMPOTENCY_KEY_REUSE_MISMATCH", "Khóa Idempotency-Key đã được sử dụng trước đó cho một yêu cầu có nội dung khác."));
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Result<ReviewDetailResponse>> WithdrawReviewAsync(
        ReviewActorContext actor,
        Guid reviewId,
        WithdrawReviewRequest? request,
        string idempotencyKey,
        string? ifMatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var actorError = ValidateActor(actor.ActorId, actor.UserType);
        if (actorError != null)
        {
            return Result.Failure<ReviewDetailResponse>(actorError);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.Validation("IDEMPOTENCY_KEY_REQUIRED", "Khóa Idempotency-Key là bắt buộc đối với yêu cầu này."));
        }

        // 1. Kiểm tra Idempotency nhanh (pre-check) ngoài transaction
        string requestPath = $"/api/v1/reviews/{reviewId}/withdraw";
        string requestBody = request != null ? JsonSerializer.Serialize(request, JsonOptions) : string.Empty;
        string fingerprint = _idempotencyService.ComputeFingerprint(actor.ActorId, "POST", requestPath, requestBody, ifMatch);
        var checkResult = await _idempotencyService.CheckAsync(
            actor.ActorId,
            idempotencyKey,
            fingerprint,
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

        // 2. Mở transaction và lấy PostgreSQL transaction advisory lock theo cặp (actorId, idempotencyKey)
        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            await _idempotencyRepository.AcquireLockAsync(actor.ActorId, idempotencyKey, cancellationToken);

            // 3. Đọc lại receipt sau khi lấy khóa (Replay check TRƯỚC ETag check)
            var lockCheck = await _idempotencyService.CheckAsync(
                actor.ActorId,
                idempotencyKey,
                fingerprint,
                cancellationToken);

            if (lockCheck.IsFailure)
            {
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return Result.Failure<ReviewDetailResponse>(lockCheck.Error);
            }

            if (lockCheck.Value != null)
            {
                var cachedDto = JsonSerializer.Deserialize<ReviewDetailResponse>(lockCheck.Value.ResponseBody, JsonOptions);
                if (cachedDto != null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    _unitOfWork.DiscardChanges();
                    _logger?.LogInformation("Trả về kết quả rút review đã lưu (Replay under lock) cho key {Key}", idempotencyKey);
                    return Result.Success(cachedDto);
                }
            }

            // 4. Tìm review hiện tại
            var review = await _reviewRepository.GetByIdAsync(reviewId, cancellationToken);
            if (review == null)
            {
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return Result.Failure<ReviewDetailResponse>(
                    Error.NotFound("REVIEW_NOT_FOUND", "Không tìm thấy đánh giá đã chỉ định."));
            }

            // 5. Kiểm tra If-Match so với review.Version
            if (!ParseIfMatch(ifMatch, review.Version, out var ifMatchError))
            {
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return Result.Failure<ReviewDetailResponse>(ifMatchError!);
            }

            // 6. Kiểm tra quyền sở hữu tác giả
            if (actor.ActorId != review.AuthorId)
            {
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return Result.Failure<ReviewDetailResponse>(
                    Error.NotFound("REVIEW_NOT_FOUND", "Không tìm thấy đánh giá hoặc bạn không có quyền rút đánh giá này."));
            }

            // 7. Kiểm tra nếu đã Withdrawn
            if (review.AuthorState == AuthorState.Withdrawn)
            {
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                return Result.Failure<ReviewDetailResponse>(
                    Error.Conflict("REVIEW_ALREADY_WITHDRAWN", "Đánh giá này đã được rút lại trước đó."));
            }

            var now = _timeProvider.GetUtcNow();

            // 8. Ghi lại giá trị cũ
            string oldValuesJson = JsonSerializer.Serialize(new
            {
                authorState = review.AuthorState.ToString(),
                version = review.Version
            }, JsonOptions);

            // 9. Thực hiện rút đánh giá
            review.Withdraw(now);

            // 10. Tạo bản ghi kiểm toán ReviewAudit
            string newValuesJson = JsonSerializer.Serialize(new
            {
                authorState = review.AuthorState.ToString(),
                version = review.Version,
                withdrawnAt = review.WithdrawnAt
            }, JsonOptions);

            var audit = new ReviewAudit(
                id: Guid.NewGuid(),
                reviewId: review.Id,
                actorId: actor.ActorId,
                actorRole: review.Direction == ReviewDirection.renter_to_supplier_listing ? "Renter" : "Supplier",
                action: ReviewAuditAction.Withdrawn,
                reason: request?.Reason ?? "Author withdrew review",
                oldValues: oldValuesJson,
                newValues: newValuesJson,
                createdAt: now);

            // 11. Khởi tạo IdempotencyRecord
            var responseDto = ReviewDetailResponse.FromEntity(review);
            string responseBody = JsonSerializer.Serialize(responseDto, JsonOptions);

            var idempotencyRecord = _idempotencyService.CreateRecord(
                actorId: actor.ActorId,
                idempotencyKey: idempotencyKey,
                requestFingerprint: fingerprint,
                httpMethod: "POST",
                requestPath: requestPath,
                responseStatusCode: 200,
                responseHeaders: JsonSerializer.Serialize(new { ETag = $"\"{review.Version}\"" }),
                responseBody: responseBody,
                createdAt: now);

            var withdrawOutbox = new OutboxMessage(
                Guid.NewGuid(),
                ReviewEventFactory.PublicEventsTopic,
                "ReviewWithdrawn",
                review.ListingId.ToString("D"),
                JsonSerializer.Serialize(ReviewEventFactory.CreateContributionEvent(review), JsonOptions),
                now);

            // 12. Lưu nguyên tử qua IUnitOfWork
            await _reviewRepository.UpdateAsync(review, cancellationToken);
            await _reviewAuditRepository.AddAsync(audit, cancellationToken);
            await _outboxRepository.AddAsync(withdrawOutbox, cancellationToken);
            await _idempotencyRepository.SaveAsync(idempotencyRecord, cancellationToken);

            await _unitOfWork.CommitAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return Result.Success(responseDto);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            _unitOfWork.DiscardChanges();
            return Result.Failure<ReviewDetailResponse>(
                Error.PreconditionFailed("ETAG_PRECONDITION_FAILED", "Phiên bản của đánh giá đã bị thay đổi bởi thao tác khác."));
        }
        catch (DbUpdateException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            _unitOfWork.DiscardChanges();

            if (IsIdempotencyUniqueConstraintViolation(ex))
            {
                var recheck = await _idempotencyService.CheckAsync(actor.ActorId, idempotencyKey, fingerprint, cancellationToken);
                if (recheck.IsSuccess && recheck.Value != null)
                {
                    var cached = JsonSerializer.Deserialize<ReviewDetailResponse>(recheck.Value.ResponseBody, JsonOptions);
                    if (cached != null) return Result.Success(cached);
                }
                return Result.Failure<ReviewDetailResponse>(recheck.Error ?? Error.Conflict("IDEMPOTENCY_KEY_REUSE_MISMATCH", "Khóa Idempotency-Key đã được sử dụng trước đó cho một yêu cầu có nội dung khác."));
            }

            throw;
        }
    }

    private static Error? ValidateActor(Guid actorId, string? userType)
    {
        if (actorId == Guid.Empty)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Mã người dùng không hợp lệ.");
        }

        if (string.Equals(userType, "svc", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Forbidden("FORBIDDEN_SERVICE_TOKEN", "Service token không được phép thực hiện thao tác đánh giá.");
        }

        return null;
    }

    private static bool ParseIfMatch(string? ifMatch, long currentVersion, out Error? error)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            error = Error.PreconditionRequired("PRECONDITION_REQUIRED", "Header If-Match chứa ETag phiên bản là bắt buộc đối với thao tác này.");
            return false;
        }

        string cleaned = ifMatch.Trim();

        if (cleaned == "*")
        {
            error = Error.Validation("INVALID_IF_MATCH", "Header If-Match không được sử dụng ký tự đại diện (*).");
            return false;
        }

        if (cleaned.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            error = Error.Validation("WEAK_ETAG_NOT_SUPPORTED", "Hệ thống chỉ chấp nhận Strong ETag dạng số nguyên phiên bản, không chấp nhận Weak ETag (W/).");
            return false;
        }

        cleaned = cleaned.Trim('"');

        if (!long.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedVersion) || parsedVersion <= 0)
        {
            error = Error.Validation("INVALID_IF_MATCH", "Giá trị If-Match không hợp lệ. Phải là số nguyên phiên bản dương trong dấu ngoặc kép.");
            return false;
        }

        if (parsedVersion != currentVersion)
        {
            error = Error.PreconditionFailed(
                "ETAG_PRECONDITION_FAILED",
                $"Phiên bản đánh giá không khớp. Phiên bản cung cấp là {parsedVersion}, phiên bản hiện tại là {currentVersion}.");
            return false;
        }

        error = null;
        return true;
    }

    private static bool IsReviewUniqueConstraintViolation(DbUpdateException ex)
    {
        if (ex.InnerException is Npgsql.PostgresException pgEx)
        {
            return pgEx.SqlState == "23505" &&
                   (pgEx.ConstraintName?.Contains("uq_reviews", StringComparison.OrdinalIgnoreCase) == true ||
                    pgEx.ConstraintName?.Contains("direction", StringComparison.OrdinalIgnoreCase) == true);
        }
        var msg = ex.Message + (ex.InnerException?.Message ?? string.Empty);
        return msg.Contains("uq_reviews", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("order_id", StringComparison.OrdinalIgnoreCase) && msg.Contains("item_id", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIdempotencyUniqueConstraintViolation(DbUpdateException ex)
    {
        if (ex.InnerException is Npgsql.PostgresException pgEx)
        {
            return pgEx.SqlState == "23505" &&
                   (pgEx.ConstraintName?.Contains("idempotency", StringComparison.OrdinalIgnoreCase) == true);
        }
        var msg = ex.Message + (ex.InnerException?.Message ?? string.Empty);
        return msg.Contains("uq_idempotency", StringComparison.OrdinalIgnoreCase);
    }
}
