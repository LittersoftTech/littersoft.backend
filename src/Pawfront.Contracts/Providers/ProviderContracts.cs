namespace Pawfront.Contracts.Providers;

public sealed record CreateProviderRequest(
    string BusinessName,
    string OwnerName,
    string Email,
    string PhoneNumber);

public sealed record ProviderResponse(
    Guid Id,
    string BusinessName,
    string OwnerName,
    string Email,
    string PhoneNumber,
    string Status,
    DateTimeOffset CreatedAt);
