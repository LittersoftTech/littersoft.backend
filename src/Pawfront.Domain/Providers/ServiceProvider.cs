namespace Pawfront.Domain.Providers;

public sealed class ServiceProvider
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string BusinessName { get; init; }
    public required string OwnerName { get; init; }
    public required string Email { get; init; }
    public required string PhoneNumber { get; init; }
    public ProviderStatus Status { get; set; } = ProviderStatus.PendingOnboarding;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
