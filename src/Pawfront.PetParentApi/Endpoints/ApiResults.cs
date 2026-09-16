using Pawfront.Contracts.Common;

namespace Pawfront.PetParentApi.Endpoints;

/// <summary>
/// Pet-parent host equivalent of <see cref="Results"/>. Every helper wraps the
/// payload in an <see cref="ApiResponse{T}"/> envelope so the mobile client
/// always sees the same shape as the provider host.
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

    /// <summary>
    /// A 409 that also carries a payload. The envelope keeps its usual shape —
    /// <c>success: false</c> with the <c>error</c> populated — and <c>data</c>
    /// carries what the client needs to resolve the conflict, rather than
    /// stuffing a list into the error's message. Used where a refusal is only
    /// actionable with the blocking records in hand (the pet-parent account
    /// delete's pending jobs).
    /// </summary>
    public static IResult Conflict<T>(string code, string message, T data) =>
        Results.Json(
            new ApiResponse<T>(false, data, new ApiError(code, message)),
            statusCode: StatusCodes.Status409Conflict);

    public static IResult Forbidden(string code, string message) =>
        Results.Json(
            new ApiResponse<object>(false, default, new ApiError(code, message)),
            statusCode: StatusCodes.Status403Forbidden);
}
