namespace Pawfront.Contracts.Blocks;

/// <summary>
/// Blocks somebody. The counterparty's SIDE is never sent -- a block only ever
/// runs provider &lt;-&gt; parent, so the server derives it from whichever app the
/// caller is authenticated as. Accepting it would only create a way to get it
/// wrong.
/// </summary>
/// <param name="Reason">
/// Optional free text the blocker may record for themselves. Never shown to the
/// blocked party.
/// </param>
public sealed record BlockParticipantRequest(Guid CounterpartyId, string? Reason);

/// <summary>
/// One entry on the caller's blocked list.
/// </summary>
/// <param name="Name">
/// The blocked person's own name, joined live -- so an account deleted since
/// reads "Deleted Provider" / "Deleted User" rather than keeping its real name.
/// </param>
/// <param name="BusinessName">
/// The blocked PROVIDER's business ("Happy Paws Hotel"). Null for a pet parent,
/// for a freelancer trading under their own name, and when the offering document
/// cannot be read -- the list is served either way rather than failing over a
/// missing name. Clients should show it in preference to <paramref name="Name"/>
/// where present, since it is what the parent recognises.
/// </param>
public sealed record BlockedParticipantResponse(
    Guid BlockId,
    string BlockedType,
    Guid BlockedId,
    string? Name,
    string? BusinessName,
    string? PhotoUrl,
    string? Reason,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// What placing a block did.
/// </summary>
/// <param name="WasAlreadyBlocked">
/// True when the block was already in place, in which case nothing was cancelled
/// -- the first block dealt with the pair's jobs.
/// </param>
/// <param name="CancelledBookingCount">
/// How many of the pair's unfinished bookings the block cancelled. Worth showing:
/// blocking is not a passive setting, and a user should see that it ended work
/// the other party had agreed to do.
/// </param>
/// <param name="UncancelledBookings">
/// Bookings the block could NOT cancel, each saying why. In practice this is the
/// job already underway -- a pet in someone's care, which the status engine
/// refuses to cancel and which therefore runs to completion. Empty on the
/// ordinary path; when it is not, the app should tell the user rather than let
/// them discover a live booking with someone they have just blocked.
/// </param>
public sealed record BlockParticipantResponse(
    BlockedParticipantResponse Block,
    bool WasAlreadyBlocked,
    int CancelledBookingCount,
    IReadOnlyList<BlockUncancelledBookingResponse> UncancelledBookings);

/// <param name="ErrorCode">
/// The same per-item vocabulary the bulk-cancel endpoint returns --
/// <c>BookingInProgress</c> being the one to expect here.
/// </param>
public sealed record BlockUncancelledBookingResponse(
    Guid BookingId,
    string BookingType,
    string JobId,
    string? Status,
    string? ErrorCode,
    string? Message);

/// <summary>One page of the people the caller has blocked, newest first.</summary>
/// <remarks>
/// Only blocks the caller PLACED. One placed against them is never listed:
/// telling somebody they have been blocked confirms the other party acted, which
/// is the thing a block is meant to end.
/// </remarks>
public sealed record BlockedParticipantsPageResponse(
    IReadOnlyList<BlockedParticipantResponse> Blocks,
    int TotalCount,
    int Skip,
    int Take,
    bool HasMore);
