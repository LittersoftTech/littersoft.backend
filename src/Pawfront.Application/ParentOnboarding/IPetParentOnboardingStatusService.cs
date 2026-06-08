namespace Pawfront.Application.ParentOnboarding;

public interface IPetParentOnboardingStatusService
{
    /// <summary>
    /// Computes the pet-parent's onboarding progress. Throws
    /// <see cref="PetParentOnboardingStatusNotFoundException"/> when the
    /// parent profile row is missing.
    /// </summary>
    Task<PetParentOnboardingStatus> GetAsync(
        Guid petParentId,
        CancellationToken cancellationToken);
}

public interface IPetParentOnboardingStatusReader
{
    Task<PetParentOnboardingStatusSnapshot?> ReadAsync(
        Guid petParentId,
        CancellationToken cancellationToken);
}
