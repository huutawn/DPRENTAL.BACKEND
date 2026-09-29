namespace Dprental.MediaService.Application.DTOs;

public sealed record UploadFileErrorDto(string Code, string Message);

public sealed record UploadFileResultDto(
    int Index,
    string FileName,
    MediaCompletedDto? Media,
    UploadFileErrorDto? Error);
