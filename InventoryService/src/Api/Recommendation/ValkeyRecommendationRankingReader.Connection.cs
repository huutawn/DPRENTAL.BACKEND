using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Dprental.InventoryService.Api.Recommendation;

public sealed partial class ValkeyRecommendationRankingReader
{
    private async Task<IConnectionMultiplexer?> GetMultiplexerAsync(CancellationToken cancellationToken)
    {
        if (!_isConfigured) return null;
        if (_multiplexer != null && _multiplexer.IsConnected) return _multiplexer;
        if (!_ownsMultiplexer) return _multiplexer;

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_multiplexer != null && _multiplexer.IsConnected) return _multiplexer;

            if (_multiplexer != null)
            {
                await _multiplexer.DisposeAsync();
                _multiplexer = null;
            }

            var connecting = ConnectionMultiplexer.ConnectAsync(_configurationOptions!);
            try
            {
                _multiplexer = await connecting.WaitAsync(cancellationToken);
            }
            catch
            {
                _ = connecting.ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        try { task.Result.Dispose(); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose a late Recommendation Valkey connection."); }
                    }
                    else { _ = task.Exception; }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            return _multiplexer;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to Recommendation Valkey.");
            return null;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ownsMultiplexer && _multiplexer != null)
        {
            await _multiplexer.DisposeAsync();
            _multiplexer = null;
        }

        _connectLock.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ownsMultiplexer && _multiplexer != null)
        {
            _multiplexer.Dispose();
            _multiplexer = null;
        }

        _connectLock.Dispose();
    }
}
