using System.Text;
using Dprental.MediaService.Application.Policies;
using Xunit;

namespace Dprental.MediaService.Api.Tests;

public class PolicyAndSanitizerTests
{
    [Theory]
    [InlineData("equipment-image", true, 10 * 1024 * 1024)]
    [InlineData("organization-logo", true, 10 * 1024 * 1024)]
    [InlineData("equipment-document", true, 20 * 1024 * 1024)]
    [InlineData("unknown-purpose", false, 0)]
    [InlineData("", false, 0)]
    [InlineData(null, false, 0)]
    public void MediaPurposePolicy_Lookup_ShouldReturnExpected(string? purpose, bool expectedValid, long expectedMaxSize)
    {
        var policy = MediaPurposePolicy.GetPolicy(purpose);
        if (expectedValid)
        {
            Assert.NotNull(policy);
            Assert.Equal(expectedMaxSize, policy.MaxSizeBytes);
        }
        else
        {
            Assert.Null(policy);
        }
    }

    [Theory]
    [InlineData("equipment-image", "image/jpeg", true)]
    [InlineData("equipment-image", "image/png", true)]
    [InlineData("equipment-image", "image/webp", true)]
    [InlineData("equipment-image", "application/pdf", false)]
    [InlineData("equipment-image", "application/x-msdownload", false)]
    [InlineData("equipment-document", "application/pdf", true)]
    [InlineData("equipment-document", "image/jpeg", false)]
    public void MediaPurposePolicy_ContentType_ShouldMatchSpec(string purpose, string contentType, bool expectedAllowed)
    {
        var policy = MediaPurposePolicy.GetPolicy(purpose);
        Assert.NotNull(policy);
        Assert.Equal(expectedAllowed, policy.IsContentTypeAllowed(contentType));
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
