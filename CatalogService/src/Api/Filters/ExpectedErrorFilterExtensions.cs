using Dprental.CatalogService.Common;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.CatalogService.Api.Filters;

/// <summary>Chuyển expected error của Application/Domain thành HTTP ProblemDetails tại API boundary.</summary>
public static class ExpectedErrorFilterExtensions
{
    /// <summary>Trả về HTTP status phù hợp với loại lỗi mà không đưa business logic vào controller.</summary>
    public static ObjectResult ToProblem(this ControllerBase controller, Error error)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(error);

        return controller.Problem(
            statusCode: error.Type switch
            {
                ErrorType.Validation => StatusCodes.Status400BadRequest,
                ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
                ErrorType.Forbidden => StatusCodes.Status403Forbidden,
                ErrorType.NotFound => StatusCodes.Status404NotFound,
                ErrorType.Conflict => StatusCodes.Status409Conflict,
                ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status500InternalServerError,
            },
            title: error.Code,
            detail: error.Message);
    }
}
