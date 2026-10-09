using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Filters;

/// <summary>Chuyển expected error của Application/Domain thành HTTP problem details tại API boundary.</summary>
public static class ExpectedErrorFilterExtensions
{
    /// <summary>Trả về HTTP status phù hợp với loại lỗi mà không đưa business logic vào controller.</summary>
    public static ObjectResult ToProblem(this ControllerBase controller, Error error)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(error);

        if (error == Error.None)
        {
            throw new ArgumentException("A successful result has no HTTP error response.", nameof(error));
        }

        return controller.Problem(
            statusCode: error.Type switch
            {
                ErrorType.Validation => StatusCodes.Status400BadRequest,
                ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
                ErrorType.Forbidden => StatusCodes.Status403Forbidden,
                ErrorType.NotFound => StatusCodes.Status404NotFound,
                ErrorType.Expired => StatusCodes.Status410Gone,
                ErrorType.Unprocessable => StatusCodes.Status422UnprocessableEntity,
                ErrorType.Conflict => StatusCodes.Status409Conflict,
                ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status500InternalServerError,
            },
            title: error.Code,
            detail: error.Message);
    }
}
