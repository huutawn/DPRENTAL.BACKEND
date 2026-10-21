using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.Entities;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Dprental.PaymentService.Domain.Repositories;

public sealed class SensitiveDataMaterializationInterceptor : IMaterializationInterceptor
{
    private readonly IPaymentDataEncryptor _encryptor;

    public SensitiveDataMaterializationInterceptor(IPaymentDataEncryptor encryptor)
    {
        _encryptor = encryptor ?? throw new ArgumentNullException(nameof(encryptor));
    }

    public object InitializedInstance(
        MaterializationInterceptionData materializationData,
        object entity)
    {
        if (entity is PaymentTransaction pt)
        {
            if (SensitiveFieldEncryptor.IsEncrypted(pt.AccountNumber))
            {
                string idStr = pt.Id.ToString("D");
                string decAccount = SensitiveFieldEncryptor.Decrypt(_encryptor, "PaymentTransaction", idStr, "AccountNumber", pt.AccountNumber) ?? string.Empty;
                string? decSub = SensitiveFieldEncryptor.Decrypt(_encryptor, "PaymentTransaction", idStr, "SubAccount", pt.SubAccount);
                string? decContent = SensitiveFieldEncryptor.Decrypt(_encryptor, "PaymentTransaction", idStr, "Content", pt.Content);
                pt.SetDecryptedSensitiveFields(decAccount, decSub, decContent);
            }
        }
        return entity;
    }
}
