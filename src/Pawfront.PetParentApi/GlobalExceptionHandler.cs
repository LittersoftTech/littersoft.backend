using Microsoft.AspNetCore.Diagnostics;
using Pawfront.Contracts.Common;

namespace Pawfront.PetParentApi;

internal sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
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
