#pragma warning disable CA1848
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Services.Integrity;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.IRepositories;
using Dprental.PaymentService.Domain.Repositories;
using Dprental.PaymentService.IntegrityHost.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.IntegrityHost.Services;

public sealed class IntegrityConfirmationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<IntegrityHostOptions> _options;
    private readonly ILogger<IntegrityConfirmationWorker> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IntegrityKeyManager? _fallbackKeyManager;
    private readonly IPaymentDataEncryptor? _fallbackEncryptor;

    public IntegrityConfirmationWorker(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<IntegrityHostOptions> options,
        ILogger<IntegrityConfirmationWorker> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IntegrityConfirmationWorker(
        IServiceScopeFactory scopeFactory,
        IntegrityKeyManager keyManager,
        IPaymentDataEncryptor encryptor,
        IOptionsMonitor<IntegrityHostOptions> options,
        ILogger<IntegrityConfirmationWorker> logger,
        TimeProvider? timeProvider = null)
        : this(scopeFactory, options, logger, timeProvider)
    {
        _fallbackKeyManager = keyManager;
        _fallbackEncryptor = encryptor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IntegrityConfirmationWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int confirmedCount = await ProcessConfirmationBatchAsync(stoppingToken);
                if (confirmedCount == 0)
                {
                    int delayMs = _options.CurrentValue.ConfirmationIntervalMs > 0
                        ? _options.CurrentValue.ConfirmationIntervalMs
                        : 250;
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _timeProvider, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing integrity confirmation batch.");
                await Task.Delay(TimeSpan.FromMilliseconds(1000), _timeProvider, stoppingToken);
            }
        }

        _logger.LogInformation("IntegrityConfirmationWorker stopped.");
    }

    public async Task<int> ProcessConfirmationBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetService<IIntegrityConfirmationService>();
        if (service is not null)
        {
            return await service.ProcessConfirmationBatchAsync(cancellationToken);
        }

        var evidenceRepo = scope.ServiceProvider.GetService<IIntegrityEvidenceRepository>()
            ?? new Dprental.PaymentService.Domain.Repositories.Integrity.EfIntegrityEvidenceRepository(scope.ServiceProvider.GetRequiredService<IntegrityDbContext>());
        var paymentRepo = scope.ServiceProvider.GetRequiredService<ITransactionJournalRepository>();
        var keyManager = (IIntegritySigningAdapter?)scope.ServiceProvider.GetService<IntegrityKeyManager>() ?? _fallbackKeyManager!;
        var encryptor = scope.ServiceProvider.GetService<IPaymentDataEncryptor>() ?? _fallbackEncryptor!;
        int batchSize = _options.CurrentValue.BatchSize > 0 ? _options.CurrentValue.BatchSize : 100;

        var confirmationService = new IntegrityConfirmationService(
            evidenceRepo, paymentRepo, keyManager, encryptor, _timeProvider, null, batchSize);
        return await confirmationService.ProcessConfirmationBatchAsync(cancellationToken);
    }
}
