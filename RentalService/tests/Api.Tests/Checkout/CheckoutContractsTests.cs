using Dprental.RentalService.Application.DTOs.Checkout;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Checkout;

public sealed class CheckoutContractsTests
{
    [Fact]
    public void CheckoutRefundAccountDto_ToString_MasksAccountNumber()
    {
        // 13-digit account number -> masked to 9 asterisks + last 4 digits
        var dto = new CheckoutRefundAccountDto("VCB", "1903456789012", "TRAN VAN A");
        string str = dto.ToString();

        Assert.Contains("*********9012", str);
        Assert.DoesNotContain("190345678", str);
        Assert.Contains("VCB", str);
        Assert.Contains("TRAN VAN A", str);
    }

    [Fact]
    public void CheckoutRefundAccountDto_ToString_HandlesShortAndNullAccounts()
    {
        // <= 4 digits: all asterisks
        var shortDto = new CheckoutRefundAccountDto("MB", "1234", "LE THI B");
        string shortStr = shortDto.ToString();
        Assert.Contains("****", shortStr);
        Assert.DoesNotContain("1234", shortStr);

        // Null account number: empty
        var nullDto = new CheckoutRefundAccountDto("TCB", null, "HOANG VAN C");
        string nullStr = nullDto.ToString();
        Assert.Contains("AccountNumber = ,", nullStr);

        // Whitespace account number: empty
        var wsDto = new CheckoutRefundAccountDto("ACB", "   ", "PHAM VAN D");
        string wsStr = wsDto.ToString();
        Assert.Contains("AccountNumber = ,", wsStr);
    }

    [Fact]
    public void CheckoutRefundAccountDto_ToString_PreventsRawCredentialLogging()
    {
        const string rawAccountNumber = "098765432109876";
        var dto = new CheckoutRefundAccountDto("BIDV", rawAccountNumber, "DANG THI E");

        string loggedString = dto.ToString();
        Assert.DoesNotContain(rawAccountNumber, loggedString);
        Assert.Contains(rawAccountNumber[^4..], loggedString);
    }
}
