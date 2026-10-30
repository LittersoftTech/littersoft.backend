using Pawfront.Application.ParentOnboarding;
using Pawfront.Contracts.ParentOnboarding;
namespace Pawfront.Infrastructure.Sql.ParentOnboarding;
internal sealed class NullParentIdentityReviewService : IParentIdentityReviewService
{
    public Task<IReadOnlyList<ParentIdentityReviewItem>> ListAsync(string status, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ParentIdentityReviewItem>>([]);
    public Task ReviewAsync(Guid identityId, ReviewParentIdentityRequest request, CancellationToken cancellationToken) =>
        throw new IdentityReviewConflictException();
}
