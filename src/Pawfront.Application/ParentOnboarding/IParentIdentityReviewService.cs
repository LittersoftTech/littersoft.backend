using Pawfront.Contracts.ParentOnboarding;
namespace Pawfront.Application.ParentOnboarding;

public interface IParentIdentityReviewService
{
    Task<IReadOnlyList<ParentIdentityReviewItem>> ListAsync(string status, CancellationToken cancellationToken);
    Task ReviewAsync(Guid identityId, ReviewParentIdentityRequest request, CancellationToken cancellationToken);
}
public sealed class IdentityReviewConflictException() : Exception("Identity is missing, already reviewed, or replaced. Refresh the review queue.");
