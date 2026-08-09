using Pawfront.Application.Chat;

namespace Pawfront.ChatApi.Auth;

/// <summary>
/// Scoped-per-request wrapper over <see cref="IChatParticipantResolver"/>. The
/// first call hits SQL; the rest of the request reuses the answer, so several
/// handlers or filters in one pipeline pay the lookup once. Mirrors
/// <c>CurrentPetParentContext</c> on the pet-parent host.
/// </summary>
internal sealed class CurrentChatParticipant(
    IHttpContextAccessor httpContextAccessor,
    IChatParticipantResolver resolver) : ICurrentChatParticipant
{
    private ChatParticipant? cached;
    private bool resolved;

    public async Task<ChatParticipant?> GetParticipantAsync(CancellationToken cancellationToken)
    {
        if (resolved)
        {
            return cached;
        }

        var httpContext = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException(
                "Cannot resolve the current chat participant — no HttpContext is available. " +
                "Hub methods must use IChatParticipantResolver with Hub.Context.User instead.");

        cached = await resolver.ResolveAsync(httpContext.User, cancellationToken);
        resolved = true;
        return cached;
    }
}
