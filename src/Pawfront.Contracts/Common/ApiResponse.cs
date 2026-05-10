namespace Pawfront.Contracts.Common;

/// <summary>
/// Uniform wire envelope for every Pawfront API response.
/// On success, <see cref="Data"/> contains the payload and <see cref="Error"/> is null.
/// On failure, <see cref="Error"/> is populated and <see cref="Data"/> is null.
/// </summary>
public sealed record ApiResponse<T>(bool Success, T? Data, ApiError? Error);

public sealed record ApiError(string Code, string Message);
