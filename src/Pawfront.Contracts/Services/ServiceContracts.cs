namespace Pawfront.Contracts.Services;

public sealed record CreateServiceRequest(
    string Name,
    string? Description,
    decimal BasePrice,
    int DurationMinutes);

public sealed record ServiceResponse(
    Guid Id,
    Guid ProviderId,
    string Name,
    string? Description,
    decimal BasePrice,
    int DurationMinutes,
    bool IsActive);
