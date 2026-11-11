using System.Buffers.Binary;
using System.Numerics;
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
    private static readonly byte[] EbmlHeader = [0x1A, 0x45, 0xDF, 0xA3];
    private static readonly byte[] FtypHeader = [0x66, 0x74, 0x79, 0x70]; // "ftyp"

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

        if (IsMp4(headerBytes))
        {
            return "video/mp4";
        }

        if (IsWebM(headerBytes))
        {
            return "video/webm";
        }

        return null;
    }

    private static bool IsMp4(ReadOnlySpan<byte> headerBytes)
    {
        // Minimum ftyp box: 4 (boxSize) + 4 ("ftyp") + 4 (major_brand) + 4 (minor_version) = 16 bytes.
        if (headerBytes.Length < 16)
        {
            return false;
        }

        if (!headerBytes.Slice(4, 4).SequenceEqual(FtypHeader))
        {
            return false;
        }

        uint boxSize = BinaryPrimitives.ReadUInt32BigEndian(headerBytes.Slice(0, 4));
        // Size 0 (extends to EOF) or 1 (extended 64-bit size) is rejected.
        // Box must be at least 16 bytes, cannot exceed buffer length or max 4096.
        if (boxSize < 16 || boxSize > (uint)headerBytes.Length || boxSize > 4096)
        {
            return false;
        }

        // Remaining bytes after minor_version (offset 16) are compatible brands (groups of 4 bytes).
        int compatibleBytes = (int)boxSize - 16;
        if (compatibleBytes % 4 != 0)
        {
            return false;
        }

        // Major brand check at offset 8..11
        if (IsMp4Brand(headerBytes.Slice(8, 4)))
        {
            return true;
        }

        // Compatible brands check at 16, 20, ... (strictly within box boundary)
        for (int offset = 16; offset <= (int)boxSize - 4; offset += 4)
        {
            if (IsMp4Brand(headerBytes.Slice(offset, 4)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMp4Brand(ReadOnlySpan<byte> brand)
    {
        if (brand.Length < 4)
        {
            return false;
        }

        return brand.SequenceEqual("isom"u8) ||
               brand.SequenceEqual("iso2"u8) ||
               brand.SequenceEqual("iso3"u8) ||
               brand.SequenceEqual("iso4"u8) ||
               brand.SequenceEqual("iso5"u8) ||
               brand.SequenceEqual("iso6"u8) ||
               brand.SequenceEqual("mp41"u8) ||
               brand.SequenceEqual("mp42"u8) ||
               brand.SequenceEqual("avc1"u8) ||
               brand.SequenceEqual("dash"u8);
    }

    private static bool TryParseVInt(
        ReadOnlySpan<byte> buffer,
        bool maskLeadingBit,
        out long value,
        out int length)
    {
        value = 0;
        length = 0;

        if (buffer.IsEmpty)
        {
            return false;
        }

        byte first = buffer[0];
        if (first == 0)
        {
            return false;
        }

        int leadingZeros = BitOperations.LeadingZeroCount((uint)(first << 24));
        int vintLen = leadingZeros + 1; // 1 to 8 bytes

        if (vintLen > 8 || buffer.Length < vintLen)
        {
            return false;
        }

        byte mask = (byte)(0xFF >> vintLen);

        // Check for unknown size: all data bits are 1
        if ((first & mask) == mask)
        {
            bool allOnes = true;
            for (int i = 1; i < vintLen; i++)
            {
                if (buffer[i] != 0xFF)
                {
                    allOnes = false;
                    break;
                }
            }

            if (allOnes)
            {
                return false; // Unknown-size VINT is rejected
            }
        }

        long val = maskLeadingBit ? (first & mask) : first;
        for (int i = 1; i < vintLen; i++)
        {
            val = (val << 8) | buffer[i];
        }

        if (val < 0)
        {
            return false;
        }

        value = val;
        length = vintLen;
        return true;
    }

    private static bool IsWebM(ReadOnlySpan<byte> headerBytes)
    {
        if (headerBytes.Length < 4 || !headerBytes.Slice(0, 4).SequenceEqual(EbmlHeader))
        {
            return false;
        }

        // Parse EBML Header Size VINT
        if (!TryParseVInt(headerBytes.Slice(4), maskLeadingBit: true, out long headerBodySize, out int sizeVIntLen))
        {
            return false;
        }

        // Header body cannot be 0 bytes or negative
        if (headerBodySize <= 0)
        {
            return false;
        }

        int headerBodyStart = 4 + sizeVIntLen;
        long headerBodyEnd = (long)headerBodyStart + headerBodySize;

        // Header must be completely contained within the read buffer (and <= 4096)
        if (headerBodyEnd > headerBytes.Length || headerBodyEnd > 4096)
        {
            return false;
        }

        int offset = headerBodyStart;
        int end = (int)headerBodyEnd;
        int docTypeCount = 0;

        while (offset < end)
        {
            // Parse Child Element ID (do NOT mask leading bit for element IDs)
            if (!TryParseVInt(headerBytes.Slice(offset, end - offset), maskLeadingBit: false, out long elementId, out int idLen))
            {
                return false;
            }

            // Element ID length must be between 1 and 4 bytes
            if (idLen > 4)
            {
                return false;
            }

            offset += idLen;
            if (offset >= end)
            {
                return false;
            }

            // Parse Child Element Data Size
            if (!TryParseVInt(headerBytes.Slice(offset, end - offset), maskLeadingBit: true, out long elementSize, out int sizeLen))
            {
                return false;
            }

            offset += sizeLen;
            long dataEnd = (long)offset + elementSize;

            if (dataEnd > end || dataEnd < offset)
            {
                return false; // Element boundary overflows EBML header
            }

            // DocType Element ID: 0x4282
            if (elementId == 0x4282)
            {
                if (docTypeCount > 0)
                {
                    return false; // Duplicate DocType
                }

                docTypeCount++;

                ReadOnlySpan<byte> docTypeData = headerBytes.Slice(offset, (int)elementSize);
                if (elementSize == 4)
                {
                    if (!docTypeData.SequenceEqual("webm"u8))
                    {
                        return false;
                    }
                }
                else if (elementSize == 5 && docTypeData[4] == 0)
                {
                    if (!docTypeData.Slice(0, 4).SequenceEqual("webm"u8))
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }

            offset = (int)dataEnd;
        }

        return docTypeCount == 1;
    }

    /// <summary>
    /// Phát hiện MIME type bằng cách đọc một lượng byte đầu có giới hạn từ stream.
    /// Sau khi đọc xong, stream được rewind về vị trí ban đầu nếu stream hỗ trợ CanSeek (cả khi lỗi/hủy).
    /// </summary>
    public static async Task<string?> DetectContentTypeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        long originalPosition = stream.CanSeek ? stream.Position : 0;
        try
        {
            byte[] buffer = new byte[4096];
            int totalRead = 0;

            while (totalRead < buffer.Length)
            {
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }
                totalRead += bytesRead;
            }

            if (totalRead < 3)
            {
                return null;
            }

            return DetectContentType(buffer.AsSpan(0, totalRead));
        }
        finally
        {
            if (stream.CanSeek)
            {
                stream.Position = originalPosition;
            }
        }
    }
}
