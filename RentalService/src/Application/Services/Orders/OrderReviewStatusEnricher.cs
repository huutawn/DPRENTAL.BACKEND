#pragma warning disable CA1848, CA1873
using System.Diagnostics;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Reviews;
using Dprental.RentalService.Common;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Orders;

/// <summary>
/// Implementation điều phối làm giàu trạng thái đánh giá cho các use case đọc đơn hàng.
/// </summary>
public sealed class OrderReviewStatusEnricher : IOrderReviewStatusEnricher
{
    private readonly IReviewRentalClient _client;
    private readonly ILogger<OrderReviewStatusEnricher> _logger;

    public OrderReviewStatusEnricher(
        IReviewRentalClient client,
        ILogger<OrderReviewStatusEnricher> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyDictionary<(Guid OrderId, Guid ItemId), ReviewStatusDto>> FetchReviewStatusesAsync(
        IEnumerable<(Guid OrderId, Guid ItemId)> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        var uniquePairs = items.Distinct().ToList();
        if (uniquePairs.Count == 0)
        {
            return new Dictionary<(Guid OrderId, Guid ItemId), ReviewStatusDto>();
        }

        // Khởi tạo trạng thái mặc định UNKNOWN cho mọi phần tử được yêu cầu
        var resultMap = uniquePairs.ToDictionary(p => p, _ => ReviewStatusDto.Unknown);

        // Chia nhóm tối đa 100 item/batch
        var batches = uniquePairs.Chunk(100).ToList();

        // Tổng ngân sách enrichment tối đa 2 giây
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(2));

        var stopwatch = Stopwatch.StartNew();
        int failedBatchCount = 0;

        // Tối đa 2 request đồng thời
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = 2,
            CancellationToken = linkedCts.Token
        };

        try
        {
            await Parallel.ForEachAsync(batches, parallelOptions, async (batch, ct) =>
            {
                var request = new BatchReviewStatusRequest(
                    batch.Select(b => new ReviewStatusItemRequest(b.OrderId, b.ItemId)).ToList());

                Result<BatchReviewStatusResponse> responseResult;
                try
                {
                    responseResult = await _client.GetBatchReviewStatusAsync(request, ct);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Cancellation từ request gốc phải được truyền tiếp
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failedBatchCount);
                    _logger.LogWarning(ex, "Lỗi khi gọi batch review status cho {Count} mục", batch.Length);
                    return;
                }

                if (responseResult.IsFailure)
                {
                    Interlocked.Increment(ref failedBatchCount);
                    _logger.LogWarning("ReviewService batch status trả về thất bại: {Code}", responseResult.Error.Code);
                    return;
                }

                var response = responseResult.Value;
                if (response?.Items == null)
                {
                    Interlocked.Increment(ref failedBatchCount);
                    _logger.LogWarning("ReviewService trả về response items null");
                    return;
                }

                var requestedSet = batch.ToHashSet();

                foreach (var resItem in response.Items)
                {
                    var pair = (resItem.OrderId, resItem.ItemId);
                    // Bỏ qua phần tử ngoài request
                    if (!requestedSet.Contains(pair))
                    {
                        continue;
                    }

                    if (resItem.ReviewStatus == null || string.IsNullOrWhiteSpace(resItem.ReviewStatus.State))
                    {
                        continue;
                    }

                    string normalizedState = resItem.ReviewStatus.State.ToUpperInvariant();
                    if (normalizedState is not ("AVAILABLE" or "REVIEWED" or "EXPIRED" or "NOT_ELIGIBLE" or "UNKNOWN"))
                    {
                        continue;
                    }

                    lock (resultMap)
                    {
                        resultMap[pair] = new ReviewStatusDto(
                            normalizedState,
                            resItem.ReviewStatus.ReviewId,
                            resItem.ReviewStatus.DeadlineAt);
                    }
                }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation từ request gốc: truyền tiếp, không nuốt lỗi
            throw;
        }
        catch (OperationCanceledException)
        {
            // Ngân sách 2 giây hết hạn: giữ UNKNOWN cho các phần tử chưa kịp xử lý
            _logger.LogWarning("Enrichment reviewStatus chạm ngân sách 2 giây");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi bất ngờ trong quá trình enrichment reviewStatus");
        }
        finally
        {
            stopwatch.Stop();
        }

        int unknownCount = resultMap.Values.Count(s => s.State == "UNKNOWN");
        _logger.LogInformation(
            "ReviewStatus enrichment hoàn tất: {TotalItems} mục, {TotalBatches} batch ({FailedBatches} lỗi), {UnknownCount} UNKNOWN trong {ElapsedMs}ms",
            uniquePairs.Count, batches.Count, failedBatchCount, unknownCount, stopwatch.ElapsedMilliseconds);

        return resultMap;
    }
}

/// <summary>
/// Enricher mặc định trả về UNKNOWN cho mọi phần tử mà không gọi HTTP, dùng cho fallback và test độc lập.
/// </summary>
public sealed class DefaultOrderReviewStatusEnricher : IOrderReviewStatusEnricher
{
    public static readonly DefaultOrderReviewStatusEnricher Instance = new();

    public Task<IReadOnlyDictionary<(Guid OrderId, Guid ItemId), ReviewStatusDto>> FetchReviewStatusesAsync(
        IEnumerable<(Guid OrderId, Guid ItemId)> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        var dict = items.Distinct().ToDictionary(p => p, _ => ReviewStatusDto.Unknown);
        return Task.FromResult<IReadOnlyDictionary<(Guid OrderId, Guid ItemId), ReviewStatusDto>>(dict);
    }
}
