using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Dprental.RentalService.Api.Tests.Integration;

public sealed class ReminderBenchmarkDatabaseFixture : IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;
    public string DatabaseName { get; private set; } = string.Empty;
    public long SeedingDurationMs { get; private set; }

    public async Task InitializeAsync()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available.");
        }

        (ConnectionString, DatabaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        var options = PostgresTestDatabaseHelper.CreateOptions(ConnectionString);
        await using (var db = new RentalServiceDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        var sw = Stopwatch.StartNew();

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 180;
        cmd.CommandText = @"
            INSERT INTO rental.rental_reminder_schedules (
                ""Id"",
                rental_order_id,
                rental_item_id,
                schedule_type,
                status,
                remaining_unit_count,
                next_trigger_at,
                leased_until,
                leased_by,
                lease_token,
                retry_count,
                next_retry_at,
                overdue_day_count,
                created_at,
                updated_at
            )
            SELECT
                gen_random_uuid(),
                gen_random_uuid(),
                gen_random_uuid(),
                'Handover',
                'Active',
                1,
                CASE
                    WHEN g <= 7000 THEN NOW() - interval '1 hour'
                    WHEN g <= 9000 THEN NOW() - interval '1 hour'
                    WHEN g <= 10000 THEN NOW() - interval '1 hour'
                    ELSE NOW() + ((g % 30 + 1) * interval '1 day')
                END,
                CASE
                    WHEN g > 7000 AND g <= 9000 THEN NOW() + interval '5 minutes'
                    ELSE NULL
                END,
                CASE
                    WHEN g > 7000 AND g <= 9000 THEN 'leased-worker'
                    ELSE NULL
                END,
                CASE
                    WHEN g > 7000 AND g <= 9000 THEN gen_random_uuid()
                    ELSE NULL
                END,
                CASE
                    WHEN g > 9000 AND g <= 10000 THEN 1
                    ELSE 0
                END,
                CASE
                    WHEN g > 9000 AND g <= 10000 THEN NOW() + interval '30 minutes'
                    ELSE NULL
                END,
                0,
                NOW(),
                NOW()
            FROM generate_series(1, 1000000) g;

            ANALYZE rental.rental_reminder_schedules;
            CHECKPOINT;";

        await cmd.ExecuteNonQueryAsync();
        sw.Stop();
        SeedingDurationMs = sw.ElapsedMilliseconds;
    }

    public async Task DisposeAsync()
    {
        if (!string.IsNullOrWhiteSpace(DatabaseName))
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(DatabaseName);
        }
    }
}

[Collection("PostgreSql")]
public sealed class RentalReminderBenchmarkPostgreSqlTests : IClassFixture<ReminderBenchmarkDatabaseFixture>
{
    private readonly ReminderBenchmarkDatabaseFixture _fixture;
    private readonly ITestOutputHelper _output;

    public RentalReminderBenchmarkPostgreSqlTests(ReminderBenchmarkDatabaseFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task Benchmark_VerifyFixtureDistribution_MatchesSpecification()
    {
        _output.WriteLine($"[Fixture] 1,000,000 reminder schedules seeded and analyzed in {_fixture.SeedingDurationMs} ms.");

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                COUNT(*) AS total,
                COUNT(*) FILTER (WHERE next_trigger_at > NOW()) AS future_count,
                COUNT(*) FILTER (WHERE next_trigger_at <= NOW() AND (leased_until IS NULL OR leased_until <= NOW()) AND (next_retry_at IS NULL OR next_retry_at <= NOW())) AS due_count,
                COUNT(*) FILTER (WHERE leased_until > NOW()) AS leased_count,
                COUNT(*) FILTER (WHERE next_retry_at > NOW()) AS delayed_count
            FROM rental.rental_reminder_schedules;";

        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        long total = reader.GetInt64(0);
        long futureCount = reader.GetInt64(1);
        long dueCount = reader.GetInt64(2);
        long leasedCount = reader.GetInt64(3);
        long delayedCount = reader.GetInt64(4);

        _output.WriteLine($"[Distribution] Total: {total}, Future (99%): {futureCount}, Due: {dueCount}, Leased: {leasedCount}, Delayed: {delayedCount}");

        Assert.Equal(1_000_000L, total);
        Assert.Equal(990_000L, futureCount);
        Assert.Equal(1_000L, delayedCount);
        Assert.Equal(10_000L, dueCount + leasedCount + delayedCount);
        Assert.True(dueCount >= 6_000L, $"Expected at least 6000 due items, found {dueCount}");
        Assert.True(leasedCount >= 2_000L, $"Expected at least 2000 leased items, found {leasedCount}");
    }

    [Fact]
    public async Task ClaimDueBatch_ExplainAnalyze_UsesIndexScan_NoSeqScan_LatencyUnder50Ms()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 60;
        cmd.CommandText = @"
            EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT)
            WITH candidate AS (
                SELECT s.""Id""
                FROM rental.rental_reminder_schedules s
                WHERE s.status = 'Active'
                  AND s.remaining_unit_count > 0
                  AND (s.next_retry_at IS NULL OR s.next_retry_at <= NOW())
                  AND s.next_trigger_at IS NOT NULL
                  AND s.next_trigger_at <= NOW()
                  AND (s.leased_until IS NULL OR s.leased_until <= NOW())
                ORDER BY s.next_trigger_at, s.""Id""
                LIMIT 100
                FOR UPDATE SKIP LOCKED
            )
            UPDATE rental.rental_reminder_schedules u
            SET leased_by = 'benchmark-worker',
                lease_token = '00000000-0000-0000-0000-000000000001',
                leased_until = NOW() + (60 * interval '1 second'),
                updated_at = NOW()
            FROM candidate
            WHERE u.""Id"" = candidate.""Id""
            RETURNING u.*;";

        // Warm up query execution so dirty buffer evictions from mass insert do not skew engine latency
        await using (var warmupReader = await cmd.ExecuteReaderAsync())
        {
            while (await warmupReader.ReadAsync()) { }
        }

        var lines = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                lines.Add(reader.GetString(0));
            }
        }

        string planText = string.Join("\n", lines);
        _output.WriteLine("=== EXPLAIN (ANALYZE, BUFFERS) Plan ===");
        _output.WriteLine(planText);
        _output.WriteLine("=======================================");

        // 1. Assert Index Scan or Bitmap Index Scan on ix_rental_reminder_schedules_claim
        Assert.Contains("ix_rental_reminder_schedules_claim", planText);
        Assert.True(planText.Contains("Index Scan") || planText.Contains("Bitmap Index Scan"),
            "Expected Index Scan or Bitmap Index Scan on ix_rental_reminder_schedules_claim");

        // 2. Assert NO Seq Scan on rental_reminder_schedules
        Assert.DoesNotContain("Seq Scan on rental_reminder_schedules", planText);

        // 3. Assert Execution Time < 50ms
        var execMatch = Regex.Match(planText, @"Execution Time:\s+([\d\.]+)\s+ms");
        Assert.True(execMatch.Success, "Could not parse Execution Time from EXPLAIN ANALYZE");
        double executionTimeMs = double.Parse(execMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        var planningMatch = Regex.Match(planText, @"Planning Time:\s+([\d\.]+)\s+ms");
        double planningTimeMs = planningMatch.Success ? double.Parse(planningMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 0;

        _output.WriteLine($"[Benchmark Metrics] Planning Time: {planningTimeMs:F3} ms, Execution Time: {executionTimeMs:F3} ms");
        Assert.True(executionTimeMs < 50.0, $"Execution time {executionTimeMs} ms exceeds target SLA of 50 ms");

        // 4. Assert Buffers read bounded by batch (not scanning table pages)
        var readMatches = Regex.Matches(planText, @"read=(\d+)");
        int totalBuffersRead = readMatches.Sum(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        _output.WriteLine($"[Benchmark Metrics] Total Buffers Read: {totalBuffersRead} pages (8KB per page)");
        Assert.True(totalBuffersRead < 5000, $"Buffers read ({totalBuffersRead}) was not bounded according to batch size");
    }

    [Fact]
    public async Task ClaimDueBatchAsync_RepositoryExecution_LatencyUnder50Ms_AndClaimsCorrectBatch()
    {
        var options = PostgresTestDatabaseHelper.CreateOptions(_fixture.ConnectionString);
        await using var db = new RentalServiceDbContext(options);
        var repo = new RentalReminderScheduleRepository(db);

        // Warm up connection and EF query pipeline
        await repo.ClaimDueBatchAsync("warmup-worker-1", TimeSpan.FromSeconds(30), 10);
        await repo.ClaimDueBatchAsync("warmup-worker-2", TimeSpan.FromSeconds(30), 10);

        var sw = Stopwatch.StartNew();
        var claimed = await repo.ClaimDueBatchAsync("repo-bench-worker", TimeSpan.FromMinutes(2), 100);
        sw.Stop();

        _output.WriteLine($"[Repository Benchmark] ClaimDueBatchAsync(batch: 100) on 1M rows completed in {sw.ElapsedMilliseconds} ms (claimed {claimed.Count} rows)");

        Assert.Equal(100, claimed.Count);
        Assert.All(claimed, c =>
        {
            Assert.NotEqual(Guid.Empty, c.ScheduleId);
            Assert.NotEqual(Guid.Empty, c.LeaseToken);
            Assert.True(c.LeasedUntil > DateTimeOffset.UtcNow);
        });

        Assert.True(sw.ElapsedMilliseconds < 50, $"ClaimDueBatchAsync latency ({sw.ElapsedMilliseconds} ms) exceeded 50 ms SLA");
    }
}
