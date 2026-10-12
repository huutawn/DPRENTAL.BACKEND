using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Policies;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.RentalService.Api.Exceptions;
using Dprental.RentalService.Api.Routing;
using Dprental.RentalService.Api.Security;
using Dprental.RentalService.Application.IServices;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services;
using Dprental.RentalService.Domain.Repositories;
using Dprental.RentalService.Api.Background;
using Dprental.RentalService.Api.Clients;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.Expiry;
using Dprental.RentalService.Application.Integration;
using Dprental.RentalService.Application.Operations;
using Dprental.RentalService.Application.Payments;
using Dprental.RentalService.Application.SupplierDecision;
using Dprental.RentalService.Application.SupplierStatistics;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Application.Observability;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Dprental.RentalService.DependencyInjection;
using Dprental.RentalService.Domain.DependencyInjection;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRentalServiceDomainRepositories(builder.Configuration.GetConnectionString("Postgres"));
builder.Services.AddRentalServiceApplication();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<RentalServiceDbContext>(
        "postgres",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"]);

builder.Services.AddHttpContextAccessor();
builder.Services.AddRentalJwtAuthentication(builder.Configuration);
builder.Services.AddScoped<IRentalCallerContext>(sp =>
{
    var http = sp.GetRequiredService<IHttpContextAccessor>();
    return RentalCallerContext.FromClaimsPrincipal(http.HttpContext?.User);
});
builder.Services.AddScoped<IRentalCalendarService, RentalCalendarService>();
builder.Services.AddScoped<IRentalOperationExecutor, RentalOperationExecutor>();
builder.Services.AddHttpClient<IServiceTokenProvider, AuthServiceTokenProvider>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<IPaymentRentalClient, HttpPaymentRentalClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<IInventoryRentalClient, HttpInventoryRentalClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<InventoryRentalCartReader>();
builder.Services.AddScoped<IRentalListingOwnerProvider>(sp => sp.GetRequiredService<InventoryRentalCartReader>());
builder.Services.AddScoped<IRentalListingReader>(sp => sp.GetRequiredService<InventoryRentalCartReader>());
builder.Services.AddScoped<IRentalAvailabilityReader>(sp => sp.GetRequiredService<InventoryRentalCartReader>());
builder.Services.Configure<RentalTermsOptions>(builder.Configuration.GetSection(RentalTermsOptions.SectionName));
// Bản tạm: checkout trả 503 cho đến khi có hợp đồng đọc Listing đầy đủ với Inventory và hợp đồng với Catalog.
builder.Services.AddSingleton<IInventoryListingClient, UnavailableInventoryListingClient>();
builder.Services.AddSingleton<ICatalogReferenceClient, UnavailableCatalogReferenceClient>();
builder.Services.AddScoped<IPaymentWorkflowService, PaymentWorkflowService>();
builder.Services.AddScoped<ISupplierDecisionService, SupplierDecisionService>();
builder.Services.AddScoped<IExpiryWorkflowService, ExpiryWorkflowService>();
builder.Services.AddScoped<ISupplierStatisticsService, SupplierStatisticsService>();
builder.Services.AddScoped<IRefundCompletedConsumer, RefundCompletedConsumer>();
builder.Services.AddScoped<IPaymentEventsDispatcher, PaymentEventsDispatcher>();
builder.Services.AddSingleton<RentalMetrics>();
builder.Services.AddHostedService<OutboxPublisherWorker>();
builder.Services.AddHostedService<OperationRecoveryWorker>();
builder.Services.AddHostedService<ExpiryScannerWorker>();
builder.Services.AddHostedService<KafkaPaymentEventsConsumerWorker>();
builder.Services.AddHostedService<InboxProcessingWorker>();
builder.Services.AddSingleton<IRentalCartPolicy, RentalCartOptions>();
builder.Services.AddScoped<ICurrentRenter, CurrentRenter>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Renter", policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(context => RentalCallerContext.FromClaimsPrincipal(context.User).UserId.HasValue));
});
builder.Services.AddControllers(options =>
    options.Conventions.Add(new ApiRoutePrefixConvention(ApiRoutes.Prefix)))
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper)));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    var apiXml = Path.Combine(AppContext.BaseDirectory, $"{typeof(Program).Assembly.GetName().Name}.xml");
    if (File.Exists(apiXml))
    {
        options.IncludeXmlComments(apiXml, includeControllerXmlComments: true);
    }
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

HealthCheckOptions livenessOptions = new()
{
    Predicate = _ => false,
    AllowCachingResponses = false
};
app.MapHealthChecks("/health/live", livenessOptions).AllowAnonymous();

HealthCheckOptions readinessOptions = new()
{
    Predicate = check => check.Tags.Contains("ready", StringComparer.Ordinal),
    AllowCachingResponses = false
};
app.MapHealthChecks("/health/ready", readinessOptions).AllowAnonymous();

app.MapControllers();

app.Run();

/// <summary>
/// Điểm vào ứng dụng RentalService, được công khai để chạy kiểm thử tích hợp.
/// </summary>
public partial class Program
{
}
