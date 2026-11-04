using System.Text;
using Dprental.ReviewService.Common.Utils;
using Xunit;

namespace Dprental.ReviewService.Api.Tests;

public sealed class UnicodeRuneHelperTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("hello world", 11)]
    [InlineData("Xin chào", 8)]
    public void CountRunes_BasicStrings_ReturnsExpectedCount(string? input, int expected)
    {
        Assert.Equal(expected, UnicodeRuneHelper.CountRunes(input));
    }

    [Fact]
    public void CountRunes_VietnameseWithDiacritics_CountsAccurately()
    {
        string textNfc = "Thiết bị máy quay Sony FX3 hoạt động rất tốt!";
        // Convert to NFD (decomposed)
        string textNfd = textNfc.Normalize(NormalizationForm.FormD);

        // Under Unicode scalar values, NFC vs NFD will have scalar counts corresponding to scalar values
        int nfcCount = UnicodeRuneHelper.CountRunes(textNfc);
        int nfdCount = UnicodeRuneHelper.CountRunes(textNfd);

        Assert.Equal(45, nfcCount);
        Assert.True(nfdCount >= nfcCount);
    }

    [Fact]
    public void CountRunes_SurrogatePairsAndEmojis_CountsRunesNotUtf16Chars()
    {
        // 🎥 is U+1F3AC (1 rune, 2 UTF-16 chars / surrogate pair)
        string emoji = "🎥";
        Assert.Equal(2, emoji.Length); // UTF-16 length is 2
        Assert.Equal(1, UnicodeRuneHelper.CountRunes(emoji)); // Rune count is 1

        string combined = "Máy quay 🎥 rất đẹp 🎬";
        // "Máy quay " (9) + "🎥" (1) + " rất đẹp " (9) + "🎬" (1) = 20 runes
        Assert.Equal(20, UnicodeRuneHelper.CountRunes(combined));
    }

    [Fact]
    public void CountRunes_ReadOnlySpan_MatchesStringOverload()
    {
        string text = "Đánh giá 5 sao ⭐⭐⭐⭐⭐";
        ReadOnlySpan<char> span = text.AsSpan();

        Assert.Equal(UnicodeRuneHelper.CountRunes(text), UnicodeRuneHelper.CountRunes(span));
        Assert.Equal(0, UnicodeRuneHelper.CountRunes(ReadOnlySpan<char>.Empty));
    }

    [Fact]
    public void IsValidCommentLength_BoundaryConditions_EnforcedStrictly()
    {
        string exactly1000 = new('A', 1000);
        string over1000 = new('A', 1001);

        Assert.True(UnicodeRuneHelper.IsValidCommentLength(null));
        Assert.True(UnicodeRuneHelper.IsValidCommentLength(""));
        Assert.True(UnicodeRuneHelper.IsValidCommentLength(exactly1000));
        Assert.False(UnicodeRuneHelper.IsValidCommentLength(over1000));

        // Test with custom max limit
        Assert.True(UnicodeRuneHelper.IsValidCommentLength("12345", 5));
        Assert.False(UnicodeRuneHelper.IsValidCommentLength("123456", 5));
    }
}
