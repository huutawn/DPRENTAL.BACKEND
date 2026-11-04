using System.Text;

namespace Dprental.ReviewService.Common.Utils;

/// <summary>
/// Tiện ích hỗ trợ đếm và kiểm tra độ dài chuỗi ký tự theo Unicode Scalar Values (Runes).
/// Đảm bảo tính toán chính xác số ký tự cho tiếng Việt có dấu (NFC/NFD) và emoji (surrogate pairs).
/// </summary>
public static class UnicodeRuneHelper
{
    /// <summary>
    /// Giới hạn độ dài tối đa cho bình luận đánh giá (1.000 ký tự Unicode scalar values).
    /// </summary>
    public const int MaxCommentLength = 1000;

    /// <summary>
    /// Đếm số lượng Unicode scalar values (runes) trong chuỗi văn bản.
    /// Trả về 0 nếu chuỗi rỗng hoặc null.
    /// </summary>
    /// <param name="text">Chuỗi văn bản cần đếm.</param>
    /// <returns>Số lượng runes thực tế.</returns>
    public static int CountRunes(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int count = 0;
        foreach (Rune _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Đếm số lượng Unicode scalar values (runes) trong span ký tự.
    /// </summary>
    /// <param name="text">Span ký tự cần đếm.</param>
    /// <returns>Số lượng runes thực tế.</returns>
    public static int CountRunes(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return 0;
        }

        int count = 0;
        foreach (Rune _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Kiểm tra xem nội dung văn bản có thỏa mãn độ dài tối đa cho phép theo runes hay không.
    /// </summary>
    /// <param name="text">Chuỗi văn bản cần kiểm tra.</param>
    /// <param name="maxRunes">Số lượng runes tối đa cho phép (mặc định 1.000).</param>
    /// <returns>True nếu độ dài hợp lệ, ngược lại False.</returns>
    public static bool IsValidCommentLength(string? text, int maxRunes = MaxCommentLength)
    {
        return CountRunes(text) <= maxRunes;
    }
}
