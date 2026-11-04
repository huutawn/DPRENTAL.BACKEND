namespace Dprental.PaymentService.Domain.Entities;

/// <summary>Tài khoản nhận tiền do Rental gửi kèm lúc tạo khoản, dùng để ghép QR khi Admin chuyển khoản.</summary>
public sealed record TransferRecipient(string BankCode, string AccountNumber, string AccountHolderName);
