using System.Globalization;
using Dprental.PaymentService.Application.Clients;
using Dprental.PaymentService.Application.DependencyInjection;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Services;
using Dprental.PaymentService.Domain.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

try
{
    return await RunAsync(args);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Backfill cancelled. Earlier committed events remain; rerunning is safe.");
    return 1;
}
catch (Exception error)
{
    // Connection/HTTP exceptions may embed credentials or internal response bodies.
    Console.Error.WriteLine($"Backfill failed ({error.GetType().Name}). Earlier committed events remain; rerunning is safe.");
    return 1;
}

static async Task<int> RunAsync(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    if (!DateTimeOffset.TryParseExact(builder.Configuration["before"], "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var before) ||
        !bool.TryParse(builder.Configuration["apply"] ?? "false", out bool apply) ||
        !int.TryParse(builder.Configuration["limit"] ?? "100", CultureInfo.InvariantCulture, out int limit))
    {
        Console.Error.WriteLine("Use --before yyyy-MM-ddTHH:mm:ssZ [--apply true|false] [--limit 1..100]. Apply defaults to false.");
        return 1;
    }
    builder.Services.AddPaymentDomainPersistence(builder.Configuration["ConnectionStrings:Postgres"]!);
    builder.Services.AddPaymentApplication(builder.Configuration);
    builder.Services.AddScoped<PaidInvoiceAnalyticsBackfillService>();
    builder.Services.AddHttpClient<ITransactionIntegrityChecker, IntegrityHostClient>(client =>
    {
        client.BaseAddress = new Uri(builder.Configuration["IntegrityHost:BaseUrl"]!);
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Add("X-Api-Key", builder.Configuration["IntegrityHost:InternalApiKey"]!);
    });
    using var host = builder.Build(); // Do not start payment background workers.
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    await using var scope = host.Services.CreateAsyncScope();
    var result = await scope.ServiceProvider.GetRequiredService<PaidInvoiceAnalyticsBackfillService>()
        .RunAsync(before, limit, apply, timeout.Token);
    if (result.IsFailure)
    {
        Console.Error.WriteLine($"Backfill stopped: {result.Error.Code}. Earlier committed events remain; retry after resolving the error.");
        return 1;
    }
    var report = result.Value;
    Console.WriteLine($"Apply={apply}; Candidates={report.Candidates}; Enqueued={report.Enqueued}; Skipped={report.Skipped}");
    return 0;
}
