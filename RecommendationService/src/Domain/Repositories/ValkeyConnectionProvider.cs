using Dprental.RecommendationService.Domain.IRepositories;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Dprental.RecommendationService.Domain.Repositories;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Change type of field", Justification = "Interface abstraction")]
public sealed class ValkeyConnectionProvider : IValkeyConnectionProvider
{
    private readonly ConfigurationOptions _configurationOptions;
    private readonly ILogger<ValkeyConnectionProvider> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnectionMultiplexer? _multiplexer;
    private bool _disposed;

    public ValkeyConnectionProvider(string connectionString, ILogger<ValkeyConnectionProvider> logger)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Valkey is enabled but ConnectionStrings:Redis is missing or empty.");
        }

        try
        {
            _configurationOptions = ConfigurationOptions.Parse(connectionString);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Invalid ConnectionStrings:Redis configuration.", ex);
        }

        _configurationOptions.AbortOnConnectFail = false;
        _configurationOptions.ConnectTimeout = 3000;
        _configurationOptions.SyncTimeout = 3000;
        _configurationOptions.AsyncTimeout = 3000;

        _logger = logger;
    }

    public bool IsConnected => _multiplexer != null && _multiplexer.IsConnected;

    public async Task<IConnectionMultiplexer?> GetMultiplexerAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_multiplexer != null)
        {
            return _multiplexer;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_multiplexer != null)
            {
                return _multiplexer;
            }

            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation("Connecting to Valkey...");
            _multiplexer = await ConnectionMultiplexer.ConnectAsync(_configurationOptions);
            _logger.LogInformation("Connected to Valkey successfully.");
            return _multiplexer;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to Valkey. Will retry on subsequent requests.");
            _multiplexer = null;
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock.Dispose();
        _multiplexer?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lock.Dispose();
        if (_multiplexer != null)
        {
            await _multiplexer.DisposeAsync();
        }
    }
}
