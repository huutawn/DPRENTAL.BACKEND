using System.Security.Cryptography;
using SkiaSharp;

namespace Dprental.MediaService.Domain.Processing;

public sealed class SkiaSharpImageProcessor : IImageProcessor, IDisposable
{
    private const long MaxPixels = 40_000_000;
    private readonly SemaphoreSlim _concurrencyLimiter = new(2, 2);

    public async Task<IReadOnlyList<ProcessedImageVariant>> GenerateVariantsAsync(
        Stream sourceStream,
        string contentType,
        IReadOnlyList<ImageVariantSpec> specs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceStream);
        ArgumentNullException.ThrowIfNull(specs);

        await _concurrencyLimiter.WaitAsync(cancellationToken);
        try
        {
            return ProcessImage(sourceStream, specs);
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    private static List<ProcessedImageVariant> ProcessImage(
        Stream sourceStream,
        IReadOnlyList<ImageVariantSpec> specs)
    {
        if (sourceStream.CanSeek)
        {
            sourceStream.Position = 0;
        }

        using var skData = SKData.Create(sourceStream);
        if (skData == null || skData.IsEmpty)
        {
            throw new ImageDecodeException("Dữ liệu hình ảnh trống hoặc không đọc được.");
        }

        using var codec = SKCodec.Create(skData);
        if (codec == null)
        {
            throw new ImageDecodeException("Không thể nhận diện định dạng hình ảnh hoặc tệp bị hỏng.");
        }

        int rawWidth = codec.Info.Width;
        int rawHeight = codec.Info.Height;
        long pixelCount = (long)rawWidth * rawHeight;
        if (pixelCount > MaxPixels)
        {
            throw new ImagePixelLimitExceededException(
                $"Hình ảnh có kích thước {rawWidth}x{rawHeight} ({pixelCount:N0} pixels), vượt quá giới hạn 40 megapixel cho phép.");
        }

        using SKBitmap baseBitmap = DecodeBaseFrame(codec);
        using SKBitmap orientedBitmap = ApplyExifOrientation(baseBitmap, codec.EncodedOrigin);

        int origWidth = orientedBitmap.Width;
        int origHeight = orientedBitmap.Height;

        var results = new List<ProcessedImageVariant>(specs.Count);

        foreach (var spec in specs)
        {
            int targetWidth = Math.Min(origWidth, spec.MaxWidth);
            int targetHeight = (origWidth == targetWidth)
                ? origHeight
                : (int)Math.Max(1, Math.Round((double)origHeight * targetWidth / origWidth));

            SKBitmap? resizedToDispose = null;
            SKBitmap bitmapToEncode;

            if (targetWidth == origWidth && targetHeight == origHeight)
            {
                bitmapToEncode = orientedBitmap;
            }
            else
            {
                var resizeInfo = new SKImageInfo(targetWidth, targetHeight, orientedBitmap.ColorType, orientedBitmap.AlphaType);
                resizedToDispose = orientedBitmap.Resize(resizeInfo, SKFilterQuality.High);
                if (resizedToDispose == null)
                {
                    throw new ImageProcessingException($"Không thể thay đổi kích thước ảnh cho bản {spec.Name}.");
                }
                bitmapToEncode = resizedToDispose;
            }

            try
            {
                using var image = SKImage.FromBitmap(bitmapToEncode);
                if (image == null)
                {
                    throw new ImageEncodeException($"Không thể tạo ảnh từ bitmap cho bản {spec.Name}.");
                }

                using var encoded = image.Encode(SKEncodedImageFormat.Webp, spec.Quality);
                if (encoded == null)
                {
                    throw new ImageEncodeException($"Không thể mã hóa WebP cho bản {spec.Name}.");
                }

                byte[] bytes = encoded.ToArray();
                string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

                results.Add(new ProcessedImageVariant(
                    spec.Name,
                    targetWidth,
                    targetHeight,
                    "image/webp",
                    bytes.Length,
                    sha256,
                    bytes));
            }
            finally
            {
                resizedToDispose?.Dispose();
            }
        }

        return results;
    }

    private static SKBitmap DecodeBaseFrame(SKCodec codec)
    {
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, codec.Info.ColorType, codec.Info.AlphaType);
        var bitmap = new SKBitmap(info);
        try
        {
            var options = new SKCodecOptions(0);
            var result = codec.GetPixels(info, bitmap.GetPixels(), options);
            if (result != SKCodecResult.Success)
            {
                throw new ImageDecodeException(result == SKCodecResult.IncompleteInput
                    ? "Tệp hình ảnh bị cắt cụt hoặc không đầy đủ (IncompleteInput)."
                    : $"Không thể giải mã dữ liệu điểm ảnh (mã lỗi: {result}).");
            }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static SKBitmap ApplyExifOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            // Không cần xoay; tạo một clone hoặc dùng lại nếu source không bị dispose bên ngoài.
            // Để quản lý vòng đời nhất quán (hàm gọi using baseBitmap và using orientedBitmap),
            // nếu không xoay ta clone một bản để using cả hai không lỗi kép.
            return source.Copy();
        }

        bool swapDimensions = origin is SKEncodedOrigin.LeftTop
                                    or SKEncodedOrigin.RightTop
                                    or SKEncodedOrigin.RightBottom
                                    or SKEncodedOrigin.LeftBottom;

        int newWidth = swapDimensions ? source.Height : source.Width;
        int newHeight = swapDimensions ? source.Width : source.Height;

        var oriented = new SKBitmap(newWidth, newHeight, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(oriented);
        canvas.Clear(SKColors.Transparent);

        SKMatrix matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, source.Width, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, source.Width, 0, -1, source.Height, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, source.Height, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, source.Height, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, source.Height, -1, 0, source.Width, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, source.Width, 0, 0, 1),
            _ => SKMatrix.CreateIdentity()
        };

        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0);

        canvas.Flush();
        return oriented;
    }

    public void Dispose()
    {
        _concurrencyLimiter.Dispose();
    }
}
