using Microsoft.AspNetCore.Diagnostics;
using Pawfront.Contracts.Common;

namespace Pawfront.Api;

internal sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is Microsoft.Data.SqlClient.SqlException sql && sql.Number is >= 51460 and <= 51469)
        {
            var code = sql.Number switch
            {
                51460 => "EventNotFound", 51461 => "EventNotBookable", 51462 => "UpcomingBookingLimitReached",
                51463 => "RedFlagBookingLimitReached", 51464 => "RedFlagCannotCreateEvent",
                51465 => "EventLocked", 51466 => "EventMustBeCancelled", 51467 => "AttendanceWindowClosed",
                51468 => "InvalidEventTransition", _ => "EventAlreadyBooked"
            };
            httpContext.Response.StatusCode = sql.Number == 51460 ? 404 : 409;
            await httpContext.Response.WriteAsJsonAsync(new ApiResponse<object>(false, null, new ApiError(code, sql.Message)), cancellationToken);
            return true;
        }

        logger.LogError(
            exception,
            "Unhandled exception while processing {Method} {Path}.",
            httpContext.Request.Method,
            httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        await httpContext.Response.WriteAsJsonAsync(
            new ApiResponse<object>(
                Success: false,
                Data: default,
                Error: new ApiError("InternalServerError", "An unexpected error occurred.")),
            cancellationToken);

        return true;
    }
}
