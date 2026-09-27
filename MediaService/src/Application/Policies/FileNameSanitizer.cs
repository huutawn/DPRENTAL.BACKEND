using System.Text.RegularExpressions;

namespace Dprental.MediaService.Application.Policies;

/// <summary>
/// Chuẩn hóa và làm sạch tên tệp do client cung cấp, ngăn ngừa path traversal và ký tự độc hại.
/// </summary>
public static partial class FileNameSanitizer
{
    private const int MaxFileNameLength = 255;

    [GeneratedRegex(@"[^\w\.\-\s\(\)]", RegexOptions.Compiled)]
    private static partial Regex InvalidCharactersRegex();

    /// <summary>
    /// Làm sạch tên tệp: loại bỏ đường dẫn thư mục, path traversal (..), ký tự điều khiển, ký tự đặc biệt nguy hiểm.
    /// </summary>
    public static string Sanitize(string? rawFileName)
    {
        if (string.IsNullOrWhiteSpace(rawFileName))
        {
            return "unnamed-file";
        }

        // Loại bỏ đường dẫn thư mục nếu có
        string fileName = Path.GetFileName(rawFileName.Trim().Replace('\\', '/'));

        // Loại bỏ các ký tự null byte và ký tự điều khiển
        fileName = new string(fileName.Where(c => !char.IsControl(c) && c != '\0').ToArray());

        // Loại bỏ các đoạn path traversal lặp
        fileName = fileName.Replace("..", string.Empty, StringComparison.Ordinal);

        // Thay thế ký tự không an toàn
        fileName = InvalidCharactersRegex().Replace(fileName, "_");

        fileName = fileName.Trim();

        if (string.IsNullOrWhiteSpace(fileName) || fileName == ".")
        {
            fileName = "unnamed-file";
        }

        if (fileName.Length > MaxFileNameLength)
        {
            string extension = Path.GetExtension(fileName);
            int baseLength = MaxFileNameLength - extension.Length;
            if (baseLength > 0)
            {
                fileName = string.Concat(fileName.AsSpan(0, baseLength), extension);
            }
            else
            {
                fileName = fileName.Substring(0, MaxFileNameLength);
            }
        }

        return fileName;
    }
}
