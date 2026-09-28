using System.Text;
using Dprental.MediaService.Application.Policies;
using Xunit;

namespace Dprental.MediaService.Api.Tests;

public class PolicyAndSanitizerTests
{
    [Theory]
    [InlineData("image/jpeg", true, 10 * 1024 * 1024)]
    [InlineData("image/png", true, 10 * 1024 * 1024)]
    [InlineData("image/webp", true, 10 * 1024 * 1024)]
    [InlineData("application/pdf", true, 20 * 1024 * 1024)]
    [InlineData("application/unknown", false, 0)]
    [InlineData("", false, 0)]
    [InlineData(null, false, 0)]
    public void MediaFilePolicy_Lookup_ShouldReturnExpected(string? contentType, bool expectedValid, long expectedMaxSize)
    {
        var maxSize = MediaFilePolicy.MaxSizeBytesFor(contentType);
        if (expectedValid)
        {
            Assert.NotNull(maxSize);
            Assert.Equal(expectedMaxSize, maxSize.Value);
        }
        else
        {
            Assert.Null(maxSize);
        }
    }

    [Theory]
    [InlineData("image/jpeg", 1, true)]
    [InlineData("image/jpeg", 10 * 1024 * 1024, true)]
    [InlineData("image/jpeg", 10 * 1024 * 1024 + 1, false)]
    [InlineData("application/pdf", 20 * 1024 * 1024, true)]
    [InlineData("application/pdf", 20 * 1024 * 1024 + 1, false)]
    [InlineData("application/x-msdownload", 1, false)]
    public void MediaFilePolicy_Size_ShouldMatchSpec(string contentType, long size, bool expectedAllowed)
    {
        Assert.Equal(expectedAllowed, MediaFilePolicy.IsSizeAllowed(contentType, size));
    }

    [Fact]
    public void FileSignatureDetector_Jpeg_ShouldDetectCorrectly()
    {
        byte[] jpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];
        string? mime = FileSignatureDetector.DetectContentType(jpegBytes);
        Assert.Equal("image/jpeg", mime);
    }

    [Fact]
    public void FileSignatureDetector_Png_ShouldDetectCorrectly()
    {
        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];
        string? mime = FileSignatureDetector.DetectContentType(pngBytes);
        Assert.Equal("image/png", mime);
    }

    [Fact]
    public void FileSignatureDetector_Webp_ShouldDetectCorrectly()
    {
        byte[] webpBytes = [
            0x52, 0x49, 0x46, 0x46, // RIFF
            0x00, 0x00, 0x00, 0x00, // size
            0x57, 0x45, 0x42, 0x50  // WEBP
        ];
        string? mime = FileSignatureDetector.DetectContentType(webpBytes);
        Assert.Equal("image/webp", mime);
    }

    [Fact]
    public void FileSignatureDetector_Pdf_ShouldDetectCorrectly()
    {
        byte[] pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7\n%some content");
        string? mime = FileSignatureDetector.DetectContentType(pdfBytes);
        Assert.Equal("application/pdf", mime);
    }

    [Fact]
    public void FileSignatureDetector_InvalidBytes_ShouldReturnNull()
    {
        byte[] randomBytes = [0x00, 0x01, 0x02, 0x03, 0x04];
        string? mime = FileSignatureDetector.DetectContentType(randomBytes);
        Assert.Null(mime);
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\windows\\system32\\cmd.exe", "cmd.exe")]
    [InlineData("valid-image.jpg", "valid-image.jpg")]
    [InlineData("my file (1).png", "my file (1).png")]
    [InlineData("path/to/my-file.pdf", "my-file.pdf")]
    [InlineData("", "unnamed-file")]
    [InlineData(null, "unnamed-file")]
    public void FileNameSanitizer_ShouldRemovePathTraversal(string? input, string expectedEnding)
    {
        string sanitized = FileNameSanitizer.Sanitize(input);
        Assert.False(sanitized.Contains('/', StringComparison.Ordinal));
        Assert.False(sanitized.Contains('\\', StringComparison.Ordinal));
        Assert.False(sanitized.Contains("..", StringComparison.Ordinal));
        Assert.EndsWith(expectedEnding, sanitized, StringComparison.Ordinal);
    }
}
