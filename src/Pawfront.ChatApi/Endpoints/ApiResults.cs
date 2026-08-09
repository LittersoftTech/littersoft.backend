using Pawfront.Contracts.Common;

namespace Pawfront.ChatApi.Endpoints;

/// <summary>
/// Pawfront equivalent of <see cref="Results"/>. Every helper wraps the payload in an
/// <see cref="ApiResponse{T}"/> envelope so the mobile client always sees the same shape:
/// <c>{ "success": bool, "data": T | null, "error": { "code": string, "message": string } | null }</c>.
///
/// Same contract as the two CRUD hosts — the chat host is a third front door onto
/// the same product, so its responses must be indistinguishable in shape.
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

    public static IResult Forbidden(string code, string message) =>
        Results.Json(
            new ApiResponse<object>(false, default, new ApiError(code, message)),
            statusCode: StatusCodes.Status403Forbidden);

    public static IResult TooManyRequests(string code, string message) =>
        Results.Json(
            new ApiResponse<object>(false, default, new ApiError(code, message)),
            statusCode: StatusCodes.Status429TooManyRequests);

    /// <summary>
    /// The caller authenticated but has no chat identity — their Firebase account
    /// exists without a completed provider / pet-parent profile, so there is no id
    /// for them to send or receive as. Mirrors the pet-parent host's
    /// <c>ParentProfileNotCompleted</c>.
    /// </summary>
    public static IResult ChatProfileNotCompleted() =>
        Forbidden(
            "ChatProfileNotCompleted",
            "Finish setting up your profile before using chat.");
}
