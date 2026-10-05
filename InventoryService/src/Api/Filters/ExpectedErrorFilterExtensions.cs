using Dprental.InventoryService.Common;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.InventoryService.Api.Filters;

/// <summary>Ánh xạ lỗi dự kiến sang HTTP tại API boundary.</summary>
public static class ExpectedErrorFilterExtensions
{
    public static ObjectResult ToProblem(this ControllerBase controller, Error error)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(error);
        if (error == Error.None)
            throw new ArgumentException("Không thể chuyển Error.None thành HTTP error.", nameof(error));

        var problemResult = controller.Problem(statusCode: error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Expired => StatusCodes.Status410Gone,
            ErrorType.Unprocessable => StatusCodes.Status422UnprocessableEntity,
            ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError
        }, title: error.Code, detail: error.Message);

        if (error.Errors is { Count: > 0 } && problemResult.Value is ProblemDetails problemDetails)
        {
            problemDetails.Extensions["errors"] = error.Errors;
        }

        return problemResult;
    }
}
