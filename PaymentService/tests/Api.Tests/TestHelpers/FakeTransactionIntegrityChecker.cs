using System.Threading;
using System.Threading.Tasks;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Common;

namespace Dprental.PaymentService.Api.Tests.TestHelpers;

public sealed class FakeTransactionIntegrityChecker : ITransactionIntegrityChecker
{
    public static readonly FakeTransactionIntegrityChecker Instance = new();

    public Task<Result<TransactionVerificationResponse>> VerifyAsync(
        TransactionVerificationRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<Result<TransactionVerificationResponse>>(
            Error.Failure("IntegrityHost.Unavailable", "FakeTransactionIntegrityChecker is not configured for verification in this test."));
}
