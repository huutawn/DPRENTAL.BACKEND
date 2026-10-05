using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using StackExchange.Redis;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

/// <summary>
/// Fixture tự tạo instance Valkey riêng bằng Docker CLI trên cổng động,
/// pin image valkey/valkey:8-alpine và chỉ cleanup container do chính mình sở hữu.
/// </summary>
public sealed class ValkeyFixture : IAsyncLifetime
{
    private string _containerName = string.Empty;
    public string ConnectionString { get; private set; } = string.Empty;
    public IConnectionMultiplexer Multiplexer { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        int port = GetAvailablePort();
        _containerName = $"valkey-dpr150-{Guid.NewGuid():N}";
        ConnectionString = $"127.0.0.1:{port}";

        // Khởi động container riêng trên cổng động
        await RunDockerAsync($"run -d --name {_containerName} -p 127.0.0.1:{port}:6379 valkey/valkey:8-alpine");

        var config = ConfigurationOptions.Parse(ConnectionString);
        config.AllowAdmin = true;
        config.AbortOnConnectFail = false;
        config.ConnectTimeout = 3000;
        config.SyncTimeout = 3000;
        config.AsyncTimeout = 3000;

        // Chờ container khởi động và kết nối thành công (tối đa 5s)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                Multiplexer = await ConnectionMultiplexer.ConnectAsync(config);
                if (Multiplexer.IsConnected)
                {
                    break;
                }
            }
            catch
            {
                await Task.Delay(200);
            }
        }

        if (Multiplexer == null || !Multiplexer.IsConnected)
        {
            throw new InvalidOperationException($"Không thể kết nối đến test Valkey container {_containerName} tại {ConnectionString}.");
        }
    }

    public async Task FlushDbAsync()
    {
        if (Multiplexer != null && Multiplexer.IsConnected)
        {
            var server = Multiplexer.GetServers()[0];
            await server.FlushDatabaseAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (Multiplexer != null)
        {
            try
            {
                await Multiplexer.DisposeAsync();
            }
            catch
            {
                // Ignore disposal errors
            }
        }

        if (!string.IsNullOrEmpty(_containerName))
        {
            try
            {
                await RunDockerAsync($"rm -f {_containerName}");
            }
            catch
            {
                // Ignore container cleanup errors
            }
        }
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<string> RunDockerAsync(string arguments)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        proc.Start();
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"Docker command failed (exit code {proc.ExitCode}): {stderr}");
        }

        return stdout.Trim();
    }
}

/// <summary>
/// Định nghĩa xUnit Collection dùng chung ValkeyFixture và ngăn chạy song song giữa các test classes phụ thuộc cache.
/// </summary>
[CollectionDefinition("ValkeyIntegrationCollection", DisableParallelization = true)]
public sealed class ValkeyTestFixtureGroup : ICollectionFixture<ValkeyFixture>
{
}
