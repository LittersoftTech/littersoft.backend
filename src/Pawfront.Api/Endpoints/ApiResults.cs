using Pawfront.Contracts.Common;

namespace Pawfront.Api.Endpoints;

/// <summary>
/// Pawfront equivalent of <see cref="Results"/>. Every helper wraps the payload in an
/// <see cref="ApiResponse{T}"/> envelope so the mobile client always sees the same shape:
/// <c>{ "success": bool, "data": T | null, "error": { "code": string, "message": string } | null }</c>.
/// </summary>
internal static class ApiResults
{
    public static IResult Ok<T>(T data) =>
        Results.Ok(new ApiResponse<T>(true, data, null));

    public static IResult Created<T>(string location, T data) =>
        Results.Created(location, new ApiResponse<T>(true, data, null));

    public static IResult NotFound(string code, string message) =>
        Results.NotFound(new ApiResponse<object>(false, default, new ApiError(code, message)));

    public static IResult NotFound() =>
        NotFound("NotFound", "The requested resource was not found.");

    public static IResult BadRequest(string code, string message) =>
        Results.BadRequest(new ApiResponse<object>(false, default, new ApiError(code, message)));

    public static IResult Conflict(string code, string message) =>
        Results.Conflict(new ApiResponse<object>(false, default, new ApiError(code, message)));
}
