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
    [InlineData("video/mp4", true, 20 * 1024 * 1024)]
    [InlineData("video/webm", true, 20 * 1024 * 1024)]
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

    [Fact]
    public void FileSignatureDetector_Mp4_ShouldDetectCorrectly()
    {
        // ftyp box with isom major brand
        byte[] mp4Bytes = [
            0x00, 0x00, 0x00, 0x18, // box size = 24
            0x66, 0x74, 0x79, 0x70, // "ftyp"
            0x69, 0x73, 0x6F, 0x6D, // "isom"
            0x00, 0x00, 0x02, 0x00, // minor version
            0x6D, 0x70, 0x34, 0x31, // "mp41" compatible brand
            0x69, 0x73, 0x6F, 0x6D  // "isom" compatible brand
        ];
        string? mime = FileSignatureDetector.DetectContentType(mp4Bytes);
        Assert.Equal("video/mp4", mime);
    }

    [Fact]
    public void FileSignatureDetector_QuickTimeWithoutMp4Brand_ShouldReturnNull()
    {
        // ftyp box with "qt  " brand and no mp4 brand
        byte[] movBytes = [
            0x00, 0x00, 0x00, 0x14, // box size = 20
            0x66, 0x74, 0x79, 0x70, // "ftyp"
            0x71, 0x74, 0x20, 0x20, // "qt  "
            0x20, 0x05, 0x03, 0x00, // minor version
            0x71, 0x74, 0x20, 0x20  // "qt  "
        ];
        string? mime = FileSignatureDetector.DetectContentType(movBytes);
        Assert.Null(mime);
    }

    [Fact]
    public void FileSignatureDetector_WebM_ShouldDetectCorrectly()
    {
        // EBML header + DocType webm (total body size = 4*4 + 7 = 23 bytes = 0x97)
        byte[] webmBytes = [
            0x1A, 0x45, 0xDF, 0xA3, // EBML ID
            0x97,                   // EBML header size VINT (23 bytes)
            0x42, 0x86, 0x81, 0x01, // EBMLVersion = 1
            0x42, 0xF7, 0x81, 0x01, // EBMLReadVersion = 1
            0x42, 0xF2, 0x81, 0x04, // EBMLMaxIDLength = 4
            0x42, 0xF3, 0x81, 0x08, // EBMLMaxSizeLength = 8
            0x42, 0x82, 0x84, 0x77, 0x65, 0x62, 0x6D // DocType = "webm"
        ];
        string? mime = FileSignatureDetector.DetectContentType(webmBytes);
        Assert.Equal("video/webm", mime);
    }

    [Fact]
    public void FileSignatureDetector_EbmlMatroska_ShouldReturnNull()
    {
        // EBML header + DocType matroska (MKV, not WebM, total body size = 2 + 1 + 8 = 11 bytes = 0x8B)
        byte[] mkvBytes = [
            0x1A, 0x45, 0xDF, 0xA3, // EBML ID
            0x8B,                   // EBML header size VINT (11 bytes)
            0x42, 0x82, 0x88, 0x6D, 0x61, 0x74, 0x72, 0x6F, 0x73, 0x6B, 0x61 // DocType = "matroska"
        ];
        string? mime = FileSignatureDetector.DetectContentType(mkvBytes);
        Assert.Null(mime);
    }

    [Fact]
    public void FileSignatureDetector_Mp4BoxSize8WithBrandOutside_ShouldReturnNull()
    {
        // Reproduction case: boxSize = 8 (only covers size + ftyp), brand "isom" is outside box!
        byte[] payload = [
            0x00, 0x00, 0x00, 0x08, // box size = 8
            0x66, 0x74, 0x79, 0x70, // "ftyp"
            0x69, 0x73, 0x6F, 0x6D  // "isom" (outside the box!)
        ];
        string? mime = FileSignatureDetector.DetectContentType(payload);
        Assert.Null(mime);
    }

    [Fact]
    public void FileSignatureDetector_Mp4InvalidSizesAndAlignment_ShouldReturnNull()
    {
        // Box size 0
        byte[] sizeZero = [0x00, 0x00, 0x00, 0x00, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D, 0, 0, 0, 0];
        Assert.Null(FileSignatureDetector.DetectContentType(sizeZero));

        // Box size 1 (extended 64-bit size not supported)
        byte[] sizeOne = [0x00, 0x00, 0x00, 0x01, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D, 0, 0, 0, 0];
        Assert.Null(FileSignatureDetector.DetectContentType(sizeOne));

        // Box size exceeds buffer
        byte[] sizeExceeds = [0x00, 0x00, 0x01, 0x00, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D, 0, 0, 0, 0];
        Assert.Null(FileSignatureDetector.DetectContentType(sizeExceeds));

        // Box size with misaligned compatible brands (17 bytes -> 17 - 16 = 1 byte remaining)
        byte[] misaligned = [
            0x00, 0x00, 0x00, 0x11,
            0x66, 0x74, 0x79, 0x70,
            0x69, 0x73, 0x6F, 0x6D,
            0x00, 0x00, 0x02, 0x00,
            0xAA
        ];
        Assert.Null(FileSignatureDetector.DetectContentType(misaligned));
    }

    [Fact]
    public void FileSignatureDetector_WebMDocTypeOutsideHeaderSize0_ShouldReturnNull()
    {
        // Reproduction case: EBML Header size VINT is 0x80 (size = 0). DocType follows outside header.
        byte[] payload = [
            0x1A, 0x45, 0xDF, 0xA3, // EBML ID
            0x80,                   // EBML header size VINT = 0
            0x42, 0x82, 0x84, 0x77, 0x65, 0x62, 0x6D // DocType = "webm" (outside header!)
        ];
        string? mime = FileSignatureDetector.DetectContentType(payload);
        Assert.Null(mime);
    }

    [Fact]
    public void FileSignatureDetector_WebMWithUnknownElementBeforeDocType_ShouldSucceed()
    {
        // Header with unknown element (EBMLVersion = 1, 4 bytes) then DocType = "webm" (7 bytes).
        // Total body size = 4 + 7 = 11 bytes (0x8B).
        byte[] payload = [
            0x1A, 0x45, 0xDF, 0xA3, // EBML ID
            0x8B,                   // EBML header size VINT = 11 bytes
            0x42, 0x86, 0x81, 0x01, // EBMLVersion = 1
            0x42, 0x82, 0x84, 0x77, 0x65, 0x62, 0x6D // DocType = "webm"
        ];
        string? mime = FileSignatureDetector.DetectContentType(payload);
        Assert.Equal("video/webm", mime);
    }

    [Fact]
    public void FileSignatureDetector_WebMNullTerminatedDocType_ShouldSucceed()
    {
        // DocType = "webm\0" (5 bytes). Total body = 2 (ID) + 1 (size) + 5 = 8 bytes (0x88).
        byte[] payload = [
            0x1A, 0x45, 0xDF, 0xA3, // EBML ID
            0x88,                   // EBML header size VINT = 8 bytes
            0x42, 0x82, 0x85, 0x77, 0x65, 0x62, 0x6D, 0x00 // DocType = "webm\0"
        ];
        string? mime = FileSignatureDetector.DetectContentType(payload);
        Assert.Equal("video/webm", mime);
    }

    [Fact]
    public void FileSignatureDetector_WebMDuplicateDocType_ShouldReturnNull()
    {
        // Header with two DocTypes -> reject duplicate
        byte[] payload = [
            0x1A, 0x45, 0xDF, 0xA3, // EBML ID
            0x8E,                   // EBML header size VINT = 14 bytes
            0x42, 0x82, 0x84, 0x77, 0x65, 0x62, 0x6D,
            0x42, 0x82, 0x84, 0x77, 0x65, 0x62, 0x6D
        ];
        string? mime = FileSignatureDetector.DetectContentType(payload);
        Assert.Null(mime);
    }

    [Fact]
    public async Task FileSignatureDetector_DetectContentTypeAsync_RestoresStreamPosition()
    {
        byte[] mp4Bytes = [
            0x00, 0x00, 0x00, 0x18, // box size = 24
            0x66, 0x74, 0x79, 0x70, // "ftyp"
            0x69, 0x73, 0x6F, 0x6D, // "isom"
            0x00, 0x00, 0x02, 0x00, // minor version
            0x6D, 0x70, 0x34, 0x31, // "mp41" compatible brand
            0x69, 0x73, 0x6F, 0x6D  // "isom" compatible brand
        ];

        using var stream = new MemoryStream();
        stream.Write([1, 2, 3]);
        long initialPos = stream.Position;
        stream.Write(mp4Bytes);
        stream.Position = initialPos;

        string? mime = await FileSignatureDetector.DetectContentTypeAsync(stream);
        Assert.Equal("video/mp4", mime);
        Assert.Equal(initialPos, stream.Position);
    }

    [Fact]
    public void FileSignatureDetector_TruncatedEbml_ShouldReturnNull()
    {
        byte[] truncatedEbml = [0x1A, 0x45, 0xDF, 0xA3, 0x42];
        string? mime = FileSignatureDetector.DetectContentType(truncatedEbml);
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
