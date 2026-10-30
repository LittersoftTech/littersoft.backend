namespace Pawfront.Application.ParentOnboarding;

public interface IParentEmailStatusReader
{
    /// <summary>True only for a non-deleted pet-parent profile, not an auth identity alone.</summary>
    Task<bool> IsRegisteredAsync(string email, CancellationToken cancellationToken);
}
