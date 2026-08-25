using Pawfront.Application.Support;
using Pawfront.PetParentApi.Auth;

namespace Pawfront.PetParentApi.Endpoints;

/// <summary>
/// Resolves the caller's own open support tickets for the read paths that surface
/// <c>isTicketRaisedByMe</c> — the booking detail, both "my bookings" lists, and the event
/// list and detail.
/// </summary>
/// <remarks>
/// <para>
/// One read serves a whole page, so a list pays for it once rather than once per card. The
/// endpoints that already know their <c>petParentId</c> (everything under the
/// ownership-filtered group) pass it straight in; the event routes are not scoped to a
/// parent, so those resolve the caller from the JWT.
/// </para>
/// <para>
/// A caller with no completed profile has raised nothing, so they get
/// <see cref="MySupportTicketSubjects.Empty"/> rather than an error — the event catalog is
/// readable before onboarding finishes.
/// </para>
/// </remarks>
internal static class MySupportTickets
{
    public static Task<MySupportTicketSubjects> ForParentAsync(
        Guid petParentId,
        IMySupportTicketLookup lookup,
        CancellationToken cancellationToken)
        => petParentId == Guid.Empty
            ? Task.FromResult(MySupportTicketSubjects.Empty)
            : lookup.GetAsync(SupportRaisedByTypes.PetParent, petParentId, cancellationToken);

    public static async Task<MySupportTicketSubjects> ForCallerAsync(
        ICurrentPetParentContext currentParent,
        IMySupportTicketLookup lookup,
        CancellationToken cancellationToken)
    {
        var petParentId = await currentParent.GetPetParentIdAsync(cancellationToken);

        return petParentId is { } id
            ? await lookup.GetAsync(SupportRaisedByTypes.PetParent, id, cancellationToken)
            : MySupportTicketSubjects.Empty;
    }
}
