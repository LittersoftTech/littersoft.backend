namespace Pawfront.Domain.Services;

public sealed class PetService
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ProviderId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public decimal BasePrice { get; init; }
    public int DurationMinutes { get; init; }
    public bool IsActive { get; set; } = true;
}
