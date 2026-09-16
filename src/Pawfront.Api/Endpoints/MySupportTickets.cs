using Pawfront.Api.Auth;
using Pawfront.Application.ProviderOnboarding;
using Pawfront.Application.Support;

namespace Pawfront.Api.Endpoints;

/// <summary>
/// Resolves the caller's own open support tickets for the read paths that surface
/// <c>isTicketRaisedByMe</c> — the booking detail, the booking lists, and the event list and
/// detail.
/// </summary>
/// <remarks>
/// <para>
/// One read serves a whole page, so a list pays for it once rather than once per card.
/// </para>
/// <para>
/// The booking routes pass the route's <c>providerId</c> straight in, matching this host's
/// usual posture — it is a decoration on the provider's own screens, not revenue or a write.
/// The event routes are not scoped to a provider, so those resolve the caller from the JWT;
/// a caller with no provider identity yet has raised nothing and gets
/// <see cref="MySupportTicketSubjects.Empty"/> rather than an error, since the event catalog
/// is readable before onboarding finishes.
/// </para>
/// </remarks>
internal static class MySupportTickets
{
    public static Task<MySupportTicketSubjects> ForProviderAsync(
        Guid providerId,
        IMySupportTicketLookup lookup,
        CancellationToken cancellationToken)
        => providerId == Guid.Empty
            ? Task.FromResult(MySupportTicketSubjects.Empty)
            : lookup.GetAsync(SupportRaisedByTypes.Provider, providerId, cancellationToken);

    public static async Task<MySupportTicketSubjects> ForCallerAsync(
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        IMySupportTicketLookup lookup,
        CancellationToken cancellationToken)
    {
        try
        {
            var firebaseUserId = FirebaseClaims.GetFirebaseUserId(httpContext.User);
            var caller = await onboardingService.ResolveProviderByFirebaseUidAsync(
                firebaseUserId, cancellationToken);

            return caller.ProviderId is { } providerId
                ? await lookup.GetAsync(SupportRaisedByTypes.Provider, providerId, cancellationToken)
                : MySupportTicketSubjects.Empty;
        }
        catch (ProviderAuthIdentityForFirebaseUserNotFoundException)
        {
            // No provider identity for this Firebase user — they cannot have raised
            // anything as one.
            return MySupportTicketSubjects.Empty;
        }
    }
}
