using Dprental.PaymentService.Domain.Utils;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Unit;

public sealed class PaymentCodeGeneratorTests
{
    [Fact]
    public void Generate_ProducesExpectedFormatAndLength()
    {
        string code = PaymentCodeGenerator.Generate();

        Assert.NotNull(code);
        Assert.Equal(14, code.Length);
        Assert.StartsWith("DPR ", code, StringComparison.Ordinal);
        Assert.True(PaymentCodeGenerator.IsValid(code));
    }

    [Fact]
    public void Generate_ExcludesForbiddenCharacters()
    {
        for (int i = 0; i < 100; i++)
        {
            string code = PaymentCodeGenerator.Generate();
            string body = code[4..];

            Assert.DoesNotContain('0', body);
            Assert.DoesNotContain('1', body);
            Assert.DoesNotContain('I', body);
            Assert.DoesNotContain('O', body);
        }
    }

    [Fact]
    public void Generate_ProducesDistinctCodes()
    {
        var set = new HashSet<string>();
        for (int i = 0; i < 100; i++)
        {
            set.Add(PaymentCodeGenerator.Generate());
        }

        Assert.Equal(100, set.Count);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("DPR 123", false)]
    [InlineData("DPR ABC12345678", false)]
    [InlineData("DPR 0123456789", false)]
    [InlineData("DPR IJKLMNOPQR", false)]
    [InlineData("DPR 23456789AB", true)]
    public void IsValid_ValidatesCorrectly(string? code, bool expected)
    {
        Assert.Equal(expected, PaymentCodeGenerator.IsValid(code));
    }
}
