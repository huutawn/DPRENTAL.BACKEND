using System.Net.Http.Json;
using Dprental.NotificationService.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.NotificationService.Application.Clients;

public sealed record AuthContactDto(
    Guid UserId,
    string Email,
    bool IsActive,
    bool IsEmailVerified);

public sealed record AuthContactBatchResponse(
    IReadOnlyList<AuthContactDto>? Contacts);

public interface IAuthContactClient
{
    Task<Result<IReadOnlyList<AuthContactDto>>> GetUserContactsBatchAsync(
        IReadOnlyList<Guid> userIds,
        CancellationToken cancellationToken = default);
}

public sealed class AuthContactClient : IAuthContactClient
{
    public const string ApiKeyHeaderName = "X-Api-Key";
    public const string BatchEndpoint = "/api/v1/auth/internal/users/notification-contacts:batch";
    public const int MaxChunkSize = 100;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly ILogger<AuthContactClient> _logger;

    public AuthContactClient(HttpClient httpClient, IConfiguration configuration, ILogger<AuthContactClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var apiKey = configuration["InternalAuthentication:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Thiếu cấu hình InternalAuthentication:ApiKey.");
        }
        _apiKey = apiKey;

        try
        {
            _httpClient.Timeout = RequestTimeout;
        }
        catch (InvalidOperationException)
        {
            // Ignore if HttpClient has already started sending requests
        }
    }

    public async Task<Result<IReadOnlyList<AuthContactDto>>> GetUserContactsBatchAsync(
        IReadOnlyList<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        if (userIds == null || userIds.Count == 0)
        {
            return Result.Success<IReadOnlyList<AuthContactDto>>(Array.Empty<AuthContactDto>());
        }

        // BẤT BIẾN: Trả lỗi với Guid.Empty, không âm thầm bỏ qua
        if (userIds.Any(id => id == Guid.Empty))
        {
            return Result.Failure<IReadOnlyList<AuthContactDto>>(
                Error.Validation("INVALID_USER_ID", "Danh sách userIds không được chứa Guid.Empty."));
        }

        // Distinct và chia chunk tuần tự tối đa 100 ID / chunk để giữ tải hữu hạn
        var distinctIds = userIds.Distinct().ToList();
        var chunks = distinctIds.Chunk(MaxChunkSize).ToList();

        var aggregatedContacts = new List<AuthContactDto>();

        foreach (var chunk in chunks)
        {
            var chunkResult = await FetchChunkAsync(chunk, cancellationToken);
            if (chunkResult.IsFailure)
            {
                // Chỉ trả success khi toàn bộ chunk hợp lệ; lỗi một chunk không biến phần còn lại thành user không tồn tại!
                return chunkResult;
            }

            aggregatedContacts.AddRange(chunkResult.Value);
        }

        return Result.Success<IReadOnlyList<AuthContactDto>>(aggregatedContacts);
    }

    private async Task<Result<IReadOnlyList<AuthContactDto>>> FetchChunkAsync(
        Guid[] chunk,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BatchEndpoint);
            request.Headers.Add(ApiKeyHeaderName, _apiKey);
            request.Content = JsonContent.Create(new { userIds = chunk });

            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                _logger.LogWarning("Gọi AuthService notification-contacts:batch thất bại với mã {StatusCode}", response.StatusCode);

                // Phân biệt 401/403 (cấu hình sai, không retry) với 429/5xx (transient)
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    return Result.Failure<IReadOnlyList<AuthContactDto>>(
                        Error.Failure("AUTH_CONFIG_ERROR", $"Lỗi xác thực cấu hình AuthService ({response.StatusCode}). Vui lòng kiểm tra lại ApiKey."));
                }

                if (response.StatusCode == (System.Net.HttpStatusCode)429)
                {
                    return Result.Failure<IReadOnlyList<AuthContactDto>>(
                        Error.Failure("AUTH_RATE_LIMITED", "AuthService bị quá tải hoặc rate limited (HTTP 429)."));
                }

                if (statusCode >= 500)
                {
                    return Result.Failure<IReadOnlyList<AuthContactDto>>(
                        Error.Failure("AUTH_SERVICE_5XX", $"AuthService returned server error {response.StatusCode} ({statusCode})."));
                }

                return Result.Failure<IReadOnlyList<AuthContactDto>>(
                    Error.Failure("AUTH_SERVICE_4XX", $"AuthService returned client error {response.StatusCode} ({statusCode})."));
            }

            AuthContactBatchResponse? result;
            try
            {
                result = await response.Content.ReadFromJsonAsync<AuthContactBatchResponse>(cancellationToken: timeoutCts.Token);
            }
            catch (Exception ex)
            {
                return Result.Failure<IReadOnlyList<AuthContactDto>>(
                    Error.Failure("AUTH_MALFORMED_RESPONSE", $"Không thể phân tích dữ liệu trả về từ AuthService: {ex.Message}"));
            }

            if (result == null || result.Contacts == null)
            {
                return Result.Failure<IReadOnlyList<AuthContactDto>>(
                    Error.Failure("AUTH_MALFORMED_RESPONSE", "Body hoặc danh sách contacts từ AuthService bị null."));
            }

            // Validate contract: không duplicate user ID, không có user ngoài request
            var chunkSet = new HashSet<Guid>(chunk);
            var seenUserIds = new HashSet<Guid>();

            foreach (var contact in result.Contacts)
            {
                if (!seenUserIds.Add(contact.UserId))
                {
                    return Result.Failure<IReadOnlyList<AuthContactDto>>(
                        Error.Validation("AUTH_DUPLICATE_USER", $"AuthService trả về trùng lặp UserId: {contact.UserId}"));
                }

                if (!chunkSet.Contains(contact.UserId))
                {
                    return Result.Failure<IReadOnlyList<AuthContactDto>>(
                        Error.Validation("AUTH_EXTRA_USER", $"AuthService trả về UserId {contact.UserId} không nằm trong danh sách yêu cầu."));
                }
            }

            return Result.Success<IReadOnlyList<AuthContactDto>>(result.Contacts);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Cancellation được truyền ra ngoài
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogError(ex, "Timeout khi kết nối tới AuthService sau {TimeoutSeconds}s.", RequestTimeout.TotalSeconds);
            return Result.Failure<IReadOnlyList<AuthContactDto>>(
                Error.Failure("AUTH_NETWORK_TIMEOUT", $"Yêu cầu tới AuthService bị timeout sau {RequestTimeout.TotalSeconds} giây."));
        }
        catch (TimeoutException ex)
        {
            _logger.LogError(ex, "Timeout khi kết nối tới AuthService sau {TimeoutSeconds}s.", RequestTimeout.TotalSeconds);
            return Result.Failure<IReadOnlyList<AuthContactDto>>(
                Error.Failure("AUTH_NETWORK_TIMEOUT", $"Yêu cầu tới AuthService bị timeout sau {RequestTimeout.TotalSeconds} giây."));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Lỗi mạng khi kết nối tới AuthService: {Message}", ex.Message);
            return Result.Failure<IReadOnlyList<AuthContactDto>>(
                Error.Failure("AUTH_NETWORK_ERROR", $"Lỗi mạng khi kết nối tới AuthService: {ex.Message}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi kết nối tới AuthService khi resolve contacts batch.");
            return Result.Failure<IReadOnlyList<AuthContactDto>>(
                Error.Failure("AUTH_SERVICE_UNAVAILABLE", $"Không thể kết nối tới AuthService: {ex.Message}"));
        }
    }
}
