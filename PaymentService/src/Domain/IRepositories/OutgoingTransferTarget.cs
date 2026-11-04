namespace Dprental.PaymentService.Domain.IRepositories;

/// <summary>Payout hoặc yêu cầu hoàn tiền mà webhook tiền ra tìm theo mã chuyển khoản (DPO/DRF).</summary>
public sealed record OutgoingTransferTarget(Guid Id, decimal Amount, bool IsPending);
