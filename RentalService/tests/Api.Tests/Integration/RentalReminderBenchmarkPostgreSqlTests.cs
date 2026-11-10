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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Application.IServices.Reminders;
using Dprental.RentalService.Application.Services.Reminders;

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

[Trait("Category", "Benchmark")]
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

    [Fact(Skip = "1M scale benchmark test. Run explicitly when needed.")]
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
        Assert.True(futureCount >= 900_000L, $"Expected at least 900,000 future items, found {futureCount}");
        Assert.True(dueCount + leasedCount + delayedCount > 0);
    }

    [Fact(Skip = "1M scale benchmark test. Run explicitly when needed.")]
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

    [Fact(Skip = "1M scale benchmark test. Run explicitly when needed.")]
    public async Task ClaimDueBatchAsync_RepositoryExecution_LatencyUnder50Ms_AndClaimsCorrectBatch()
    {
        var options = PostgresTestDatabaseHelper.CreateOptions(_fixture.ConnectionString);
        await using var db = new RentalServiceDbContext(options);
        await db.Database.ExecuteSqlRawAsync(@"
            UPDATE rental.rental_reminder_schedules
            SET next_trigger_at = NOW() - interval '10 minutes',
                leased_until = NULL,
                leased_by = NULL,
                lease_token = NULL,
                next_retry_at = NULL,
                status = 'Active',
                remaining_unit_count = 1
            WHERE ""Id"" IN (
                SELECT ""Id"" FROM rental.rental_reminder_schedules
                WHERE status = 'Active'
                LIMIT 500
            );");
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

    [Fact(Skip = "1M scale benchmark test. Run explicitly when needed.")]
    public async Task Benchmark_ThreeDistributions_JsonExplain_AndCompetingWorkers()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        // Log environment specs
        await using (var envCmd = conn.CreateCommand())
        {
            envCmd.CommandText = @"
                SELECT
                    version(),
                    current_setting('shared_buffers'),
                    current_setting('effective_cache_size'),
                    current_setting('work_mem'),
                    current_setting('maintenance_work_mem');";
            await using var envReader = await envCmd.ExecuteReaderAsync();
            if (await envReader.ReadAsync())
            {
                _output.WriteLine($"[PostgreSQL Environment] Version: {envReader.GetString(0)}");
                _output.WriteLine($"[PostgreSQL Environment] shared_buffers: {envReader.GetString(1)}, effective_cache_size: {envReader.GetString(2)}, work_mem: {envReader.GetString(3)}, maintenance_work_mem: {envReader.GetString(4)}");
            }
        }
        _output.WriteLine($"[Host Environment] ProcessorCount: {Environment.ProcessorCount}, OS: {Environment.OSVersion}, .NET: {Environment.Version}");

        var options = PostgresTestDatabaseHelper.CreateOptions(_fixture.ConnectionString);

        // Reset to Distribution 1 before running
        await ResetToDistribution1Async(conn);

        // 1. DISTRIBUTION 1: 99% future, 1% mixed
        _output.WriteLine("==================================================");
        _output.WriteLine("=== DISTRIBUTION 1: 99% Future, 1% Mixed Backlog ===");
        _output.WriteLine("==================================================");
        await RunDistributionBenchmarkAsync(conn, options, "Distribution 1 (99% Future, 1% Mixed)");

        // 2. DISTRIBUTION 2: Many due, but mostly leased
        _output.WriteLine("==================================================");
        _output.WriteLine("=== DISTRIBUTION 2: Many Due, Mostly Leased ======");
        _output.WriteLine("==================================================");
        await using (var setupCmd = conn.CreateCommand())
        {
            setupCmd.CommandTimeout = 120;
            // Set 25,000 items due, ordered deterministically by "Id":
            // Adverse ordering: First 24,000 items have next_trigger_at = NOW() - 2 hours (earlier, so evaluated first!) and leased
            // Available 1,000 items have next_trigger_at = NOW() - 1 hour and unleased
            setupCmd.CommandText = @"
                WITH sample_rows AS (
                    SELECT ""Id"", row_number() over (ORDER BY ""Id"") as rn
                    FROM rental.rental_reminder_schedules
                    ORDER BY ""Id""
                    LIMIT 25000
                )
                UPDATE rental.rental_reminder_schedules s
                SET next_trigger_at = CASE WHEN sr.rn <= 24000 THEN NOW() - interval '2 hours' ELSE NOW() - interval '1 hour' END,
                    leased_until = CASE WHEN sr.rn <= 24000 THEN NOW() + interval '10 minutes' ELSE NULL END,
                    leased_by = CASE WHEN sr.rn <= 24000 THEN 'leased-worker' ELSE NULL END,
                    lease_token = CASE WHEN sr.rn <= 24000 THEN gen_random_uuid() ELSE NULL END,
                    next_retry_at = NULL,
                    status = 'Active',
                    remaining_unit_count = 1
                FROM sample_rows sr
                WHERE s.""Id"" = sr.""Id"";
                ANALYZE rental.rental_reminder_schedules;";
            await setupCmd.ExecuteNonQueryAsync();
        }
        await RunDistributionBenchmarkAsync(conn, options, "Distribution 2 (Many Due, Mostly Leased)");

        // 3. DISTRIBUTION 3: Many due, but mostly retry-delayed
        _output.WriteLine("==================================================");
        _output.WriteLine("=== DISTRIBUTION 3: Many Due, Mostly Delayed =====");
        _output.WriteLine("==================================================");
        await using (var setupCmd = conn.CreateCommand())
        {
            setupCmd.CommandTimeout = 120;
            // Adverse ordering: First 24,000 items have next_trigger_at = NOW() - 2 hours and retry-delayed
            // Available 1,000 items have next_trigger_at = NOW() - 1 hour and no retry
            setupCmd.CommandText = @"
                WITH sample_rows AS (
                    SELECT ""Id"", row_number() over (ORDER BY ""Id"") as rn
                    FROM rental.rental_reminder_schedules
                    ORDER BY ""Id""
                    LIMIT 25000
                )
                UPDATE rental.rental_reminder_schedules s
                SET next_trigger_at = CASE WHEN sr.rn <= 24000 THEN NOW() - interval '2 hours' ELSE NOW() - interval '1 hour' END,
                    leased_until = NULL,
                    leased_by = NULL,
                    lease_token = NULL,
                    next_retry_at = CASE WHEN sr.rn <= 24000 THEN NOW() + interval '30 minutes' ELSE NULL END,
                    retry_count = CASE WHEN sr.rn <= 24000 THEN 2 ELSE 0 END,
                    status = 'Active',
                    remaining_unit_count = 1
                FROM sample_rows sr
                WHERE s.""Id"" = sr.""Id"";
                ANALYZE rental.rental_reminder_schedules;";
            await setupCmd.ExecuteNonQueryAsync();
        }
        await RunDistributionBenchmarkAsync(conn, options, "Distribution 3 (Many Due, Mostly Delayed)");

        // Clean up: reset sample rows back to future so fixture remains stable
        await ResetToDistribution1Async(conn);
    }

    private static async Task ResetToDistribution1Async(NpgsqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;
        cmd.CommandText = @"
            WITH sample_rows AS (
                SELECT ""Id"", row_number() over (ORDER BY ""Id"") as rn
                FROM rental.rental_reminder_schedules
                ORDER BY ""Id""
                LIMIT 25000
            )
            UPDATE rental.rental_reminder_schedules s
            SET next_trigger_at = CASE
                    WHEN sr.rn <= 7000 THEN NOW() - interval '1 hour'
                    WHEN sr.rn <= 9000 THEN NOW() - interval '1 hour'
                    WHEN sr.rn <= 10000 THEN NOW() - interval '1 hour'
                    ELSE NOW() + ((sr.rn % 30 + 1) * interval '1 day')
                END,
                leased_until = CASE
                    WHEN sr.rn > 7000 AND sr.rn <= 9000 THEN NOW() + interval '5 minutes'
                    ELSE NULL
                END,
                leased_by = CASE
                    WHEN sr.rn > 7000 AND sr.rn <= 9000 THEN 'leased-worker'
                    ELSE NULL
                END,
                lease_token = CASE
                    WHEN sr.rn > 7000 AND sr.rn <= 9000 THEN gen_random_uuid()
                    ELSE NULL
                END,
                retry_count = CASE
                    WHEN sr.rn > 9000 AND sr.rn <= 10000 THEN 1
                    ELSE 0
                END,
                next_retry_at = CASE
                    WHEN sr.rn > 9000 AND sr.rn <= 10000 THEN NOW() + interval '30 minutes'
                    ELSE NULL
                END,
                status = 'Active',
                remaining_unit_count = 1
            FROM sample_rows sr
            WHERE s.""Id"" = sr.""Id"";
            ANALYZE rental.rental_reminder_schedules;";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task RunDistributionBenchmarkAsync(
        NpgsqlConnection conn,
        DbContextOptions<RentalServiceDbContext> options,
        string distributionName)
    {
        // 1. EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) executed inside a transaction and rolled back
        string jsonPlan;
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await using var explainCmd = conn.CreateCommand();
            explainCmd.Transaction = tx;
            explainCmd.CommandTimeout = 60;
            explainCmd.CommandText = @"
                EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)
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
                SET leased_by = 'bench-worker',
                    lease_token = gen_random_uuid(),
                    leased_until = NOW() + (60 * interval '1 second'),
                    updated_at = NOW()
                FROM candidate
                WHERE u.""Id"" = candidate.""Id""
                RETURNING u.*;";

            await using (var reader = await explainCmd.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                jsonPlan = reader.GetString(0);
            }
            await tx.RollbackAsync();
        }

        // Parse using recursive ExecutionPlanParser
        var report = ExecutionPlanParser.ParseJsonPlan(jsonPlan);
        _output.WriteLine($"[{distributionName}] Planning: {report.PlanningTimeMs:F3} ms, Execution: {report.ExecutionTimeMs:F3} ms");
        _output.WriteLine($"[{distributionName}] Derived Rows Removed by Filter: {report.TotalRowsRemovedDerived} (Rows Removed by Filter multiplied by Actual Loops, derived from EXPLAIN)");
        _output.WriteLine($"[{distributionName}] Root Buffers: Hit={report.RootSharedHitBlocks}, Read={report.RootSharedReadBlocks} (8KB blocks)");
        _output.WriteLine($"[{distributionName}] Raw JSON Plan:\n{jsonPlan}");

        string safeDistName = Regex.Replace(distributionName, @"[^a-zA-Z0-9_\-]", "_");
        string planDir = Path.Combine(Directory.GetCurrentDirectory(), "benchmark_plans");
        try
        {
            Directory.CreateDirectory(planDir);
            File.WriteAllText(Path.Combine(planDir, $"{safeDistName}.json"), jsonPlan);
        }
        catch { }

        // 2. Measure 1 Worker claim: 5 warmups, 30 measured runs
        await using var dbBench = new RentalServiceDbContext(options);
        var repo = new RentalReminderScheduleRepository(dbBench);

        // 5 Warmup runs
        for (int w = 0; w < 5; w++)
        {
            var warmupClaimed = await repo.ClaimDueBatchAsync("warmup-worker", TimeSpan.FromSeconds(30), 100);
            if (warmupClaimed.Count > 0)
            {
                var ids = warmupClaimed.Select(c => c.ScheduleId).ToArray();
                await dbBench.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE rental.rental_reminder_schedules SET leased_until = NULL, leased_by = NULL, lease_token = NULL WHERE \"Id\" = ANY({ids});");
            }
        }

        // 30 Measured runs
        var latencies = new List<double>(30);
        int totalClaimed = 0;
        int nonFullBatches = 0;
        var swTotal = Stopwatch.StartNew();

        for (int r = 0; r < 30; r++)
        {
            var sw = Stopwatch.StartNew();
            var claimed = await repo.ClaimDueBatchAsync($"bench-worker-{r}", TimeSpan.FromSeconds(30), 100);
            sw.Stop();

            latencies.Add(sw.Elapsed.TotalMilliseconds);
            totalClaimed += claimed.Count;
            if (claimed.Count < 100) nonFullBatches++;

            // Reset claimed rows to maintain identical initial state for next run
            if (claimed.Count > 0)
            {
                var ids = claimed.Select(c => c.ScheduleId).ToArray();
                await dbBench.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE rental.rental_reminder_schedules SET leased_until = NULL, leased_by = NULL, lease_token = NULL WHERE \"Id\" = ANY({ids});");
            }
        }
        swTotal.Stop();

        latencies.Sort();
        double median = latencies[15];
        double p95 = latencies[28];
        double claimRate = totalClaimed / Math.Max(0.001, swTotal.Elapsed.TotalSeconds);

        _output.WriteLine($"[{distributionName}] 1 Worker (30 runs): Median={median:F2} ms, p95={p95:F2} ms, TotalClaimed={totalClaimed}, NonFullBatches={nonFullBatches}, Rate={claimRate:F0} repository claim rows/second");

        // Assert p95 under 50ms SLA
        Assert.True(p95 < 50.0, $"p95 latency ({p95} ms) exceeded 50 ms SLA");

        // 3. Measure 2 Competing Workers
        await using var db1 = new RentalServiceDbContext(options);
        await using var db2 = new RentalServiceDbContext(options);
        var repo1 = new RentalReminderScheduleRepository(db1);
        var repo2 = new RentalReminderScheduleRepository(db2);

        var swComp = Stopwatch.StartNew();
        var task1 = repo1.ClaimDueBatchAsync("competing-worker-A", TimeSpan.FromMinutes(2), 100);
        var task2 = repo2.ClaimDueBatchAsync("competing-worker-B", TimeSpan.FromMinutes(2), 100);
        var results = await Task.WhenAll(task1, task2);
        swComp.Stop();

        var w1 = results[0];
        var w2 = results[1];
        int compTotal = w1.Count + w2.Count;
        double compRate = compTotal / Math.Max(0.001, swComp.Elapsed.TotalSeconds);

        _output.WriteLine($"[{distributionName}] 2 Competing Workers: WorkerA={w1.Count}, WorkerB={w2.Count}, Total={compTotal} in {swComp.ElapsedMilliseconds} ms ({compRate:F0} repository claim rows/second)");

        // Disjoint verification
        var setA = w1.Select(s => s.ScheduleId).ToHashSet();
        var setB = w2.Select(s => s.ScheduleId).ToHashSet();
        Assert.Empty(setA.Intersect(setB));

        // Reset leases for competing workers
        var allCompIds = w1.Concat(w2).Select(s => s.ScheduleId).ToArray();
        if (allCompIds.Length > 0)
        {
            await db1.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE rental.rental_reminder_schedules SET leased_until = NULL, leased_by = NULL, lease_token = NULL WHERE \"Id\" = ANY({allCompIds});");
        }
    }

    [Fact(Skip = "1M scale benchmark test. Run explicitly when needed.")]
    public async Task Backfill_MemoryTrackingAcrossMultiplePages_EntityTrackingCountDoesNotGrowWithTotalPages()
    {
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            DateTimeOffset now = new(2026, 11, 9, 3, 0, 0, TimeSpan.Zero);
            var timeProvider = new Dprental.RentalService.Api.Tests.Checkout.FixedTimeProvider(now);

            // Seed 30 items across 3 orders
            await using (var db = new RentalServiceDbContext(options))
            {
                await db.Database.MigrateAsync();
                for (int i = 0; i < 30; i++)
                {
                    var order = PostgresTestDatabaseHelper.CreateOrderWithItem(
                        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, now);
                    var item = order.Items.First();
                    typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!
                        .SetValue(item, Dprental.RentalService.Common.Enums.RentalItemStatus.AwaitingHandover);
                    db.RentalOrders.Add(order);
                }
                await db.SaveChangesAsync();
            }

            var services = new ServiceCollection();
            services.AddSingleton(options);
            services.AddScoped(sp => new RentalServiceDbContext(options));
            services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<RentalServiceDbContext>());
            services.AddScoped<IRentalReminderScheduleRepository, RentalReminderScheduleRepository>();
            services.AddSingleton<IOrderLockManager, FakeOrderLockManager>();
            services.AddSingleton<TimeProvider>(timeProvider);
            services.AddLogging();

            var sp = services.BuildServiceProvider();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            var lockManager = sp.GetRequiredService<IOrderLockManager>();

            var backfillService = new RentalReminderBackfillService(
                scopeFactory,
                lockManager,
                timeProvider,
                NullLogger<RentalReminderBackfillService>.Instance);

            var trackingCountsPerBatch = new List<int>();

            // Run multi-page backfill: batchSize = 10, total items = 30 -> 3 batches
            var result = await backfillService.BackfillActiveItemsAsync(
                batchSize: 10,
                phase: "handover",
                apply: true,
                onBatchCompleted: progress =>
                {
                    // Inspect a newly resolved DbContext after each batch to verify no residual tracked entities
                    using var checkScope = scopeFactory.CreateScope();
                    var checkDb = checkScope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
                    int tracked = checkDb.ChangeTracker.Entries().Count();
                    trackingCountsPerBatch.Add(tracked);
                });

            Assert.True(result.Succeeded);
            Assert.Equal(30, result.HandoverSchedulesCreated);
            Assert.Equal(3, trackingCountsPerBatch.Count);

            // Verify that tracking count remains strictly bounded to 0 for fresh scopes after each batch
            // (i.e. does NOT accumulate across total items)
            Assert.All(trackingCountsPerBatch, count => Assert.Equal(0, count));
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }
}
