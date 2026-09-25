using System.Text;

namespace Dprental.MediaService.Application.Policies;

/// <summary>
/// Xác minh định dạng tệp thực tế dựa trên magic bytes (file signature) thay vì chỉ tin vào extension hay header.
/// </summary>
public static class FileSignatureDetector
{
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] RiffHeader = [0x52, 0x49, 0x46, 0x46]; // "RIFF"
    private static readonly byte[] WebpHeader = [0x57, 0x45, 0x42, 0x50]; // "WEBP"
    private static readonly byte[] PdfHeader = [0x25, 0x50, 0x44, 0x46, 0x2D]; // "%PDF-"

    /// <summary>
    /// Phát hiện MIME type từ buffer byte đầu của tệp.
    /// Trả về null nếu không khớp với bất kỳ chữ ký tệp nào được hỗ trợ.
    /// </summary>
    public static string? DetectContentType(ReadOnlySpan<byte> headerBytes)
    {
        if (headerBytes.Length >= 3 && headerBytes.Slice(0, 3).SequenceEqual(JpegHeader))
        {
            return "image/jpeg";
        }

        if (headerBytes.Length >= 8 && headerBytes.Slice(0, 8).SequenceEqual(PngHeader))
        {
            return "image/png";
        }

        if (headerBytes.Length >= 12 &&
            headerBytes.Slice(0, 4).SequenceEqual(RiffHeader) &&
            headerBytes.Slice(8, 4).SequenceEqual(WebpHeader))
        {
            return "image/webp";
        }

        if (headerBytes.Length >= 5 && headerBytes.Slice(0, 5).SequenceEqual(PdfHeader))
        {
            return "application/pdf";
        }

        return null;
    }

    /// <summary>
    /// Phát hiện MIME type bằng cách đọc các byte đầu từ stream.
    /// Sau khi đọc xong, stream được rewind về vị trí ban đầu nếu stream hỗ trợ CanSeek.
    /// </summary>
    public static async Task<string?> DetectContentTypeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        long originalPosition = stream.CanSeek ? stream.Position : 0;
        byte[] buffer = new byte[32];
        int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);

        if (stream.CanSeek)
        {
            stream.Position = originalPosition;
        }

        if (bytesRead < 3)
        {
            return null;
        }

        return DetectContentType(buffer.AsSpan(0, bytesRead));
    }
}
