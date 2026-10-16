namespace Dprental.MediaService.Domain.Processing;

public class ImageProcessingException : Exception
{
    public ImageProcessingException(string message) : base(message) { }
    public ImageProcessingException(string message, Exception innerException) : base(message, innerException) { }
}

public class ImageDecodeException : ImageProcessingException
{
    public ImageDecodeException(string message) : base(message) { }
    public ImageDecodeException(string message, Exception innerException) : base(message, innerException) { }
}

public class ImagePixelLimitExceededException : ImageProcessingException
{
    public ImagePixelLimitExceededException(string message) : base(message) { }
}

public class ImageEncodeException : ImageProcessingException
{
    public ImageEncodeException(string message) : base(message) { }
    public ImageEncodeException(string message, Exception innerException) : base(message, innerException) { }
}
