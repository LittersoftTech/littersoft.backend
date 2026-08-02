namespace Pawfront.Application.Bookings;

/// <summary>
/// Canonical service-booking lifecycle statuses and the rules around them.
/// Stored verbatim (uppercase) in <c>Booking.Bookings.Status</c> and the audit
/// table. The stored procedure <c>Booking.UpdateBookingStatus</c> is the
/// authoritative gate for role + transition rules; these constants/sets exist
/// so the Application layer can give a clean 400 for an unknown status before
/// hitting SQL, and so callers can reason about the lifecycle.
/// </summary>
public static class BookingStatuses
{
    /// <summary>Initial state of a parent-created booking.</summary>
    public const string Created = "CREATED";

    /// <summary>Provider accepted the booking.</summary>
    public const string Confirmed = "CONFIRMED";

    /// <summary>Provider rejected the booking (terminal, frees capacity).</summary>
    public const string ProviderDeclined = "PROVIDER_DECLINED";

    /// <summary>
    /// Provider tapped "Start Job" (customer arrived): a start-OTP has been issued
    /// to the parent and the provider must enter it to move the job to
    /// <see cref="InProgress"/>. Reachable only from a confirmed-equivalent state,
    /// and only while the provider is inside their own weekly working hours.
    /// </summary>
    public const string StartJob = "START_JOB";

    /// <summary>
    /// The provider entered the parent's start-OTP — the job is now underway.
    /// From here the provider completes the job (→ <see cref="Completed"/>; no OTP).
    /// </summary>
    public const string InProgress = "IN_PROGRESS";

    /// <summary>
    /// Retired: the "End Job" intermediate state from the dual-OTP flow (an end-OTP
    /// gated completion). Completion no longer needs an OTP, so the job goes
    /// IN_PROGRESS → COMPLETED directly. Kept in <see cref="All"/> so any legacy
    /// rows stay valid; no longer settable.
    /// </summary>
    public const string Ending = "ENDING";

    /// <summary>
    /// Deprecated: the single direct "provider started the job" state. Superseded by
    /// the START_JOB → IN_PROGRESS flow. Kept in <see cref="All"/> so any
    /// legacy rows stay valid; no longer settable.
    /// </summary>
    public const string JobStarted = "JOB_STARTED";

    /// <summary>The provider marked the job done (from <see cref="InProgress"/>; no OTP).</summary>
    public const string Completed = "COMPLETED";

    /// <summary>
    /// The parent has paid the provider (set from <see cref="Completed"/> via the
    /// dedicated mark-paid endpoint). A payment ledger row is written to
    /// <c>Booking.BookingPayments</c>. Terminal; holds the booking's slot like
    /// <see cref="Completed"/>.
    /// </summary>
    public const string Paid = "PAID";

    /// <summary>Deprecated: superseded by the modification flow. Kept for legacy rows.</summary>
    public const string ApprovalNeeded = "APPROVAL_NEEDED";

    /// <summary>Parent proposed a schedule change, awaiting the provider's response.</summary>
    public const string ModificationRequestByParent = "MODIFICATION_REQUEST_BY_PARENT";

    /// <summary>Provider proposed a schedule change, awaiting the parent's response.</summary>
    public const string ModificationRequestByProvider = "MODIFICATION_REQUEST_BY_PROVIDER";

    /// <summary>Provider accepted the parent's modification (new details applied).</summary>
    public const string ProviderAcceptedModification = "PROVIDER_ACCEPTED_MODIFICATION";

    /// <summary>Provider declined the parent's modification (old details kept).</summary>
    public const string ProviderDeclinedModification = "PROVIDER_DECLINED_MODIFICATION";

    /// <summary>Parent accepted the provider's modification (new details applied).</summary>
    public const string ParentAcceptedModification = "PARENT_ACCEPTED_MODIFICATION";

    /// <summary>Parent declined the provider's modification (old details kept).</summary>
    public const string ParentDeclinedModification = "PARENT_DECLINED_MODIFICATION";

    /// <summary>Provider cancelled the booking.</summary>
    public const string ProviderCancelled = "PROVIDER_CANCELLED";

    /// <summary>Parent cancelled the booking.</summary>
    public const string ParentCancelled = "PARENT_CANCELLED";

    /// <summary>
    /// The parent (pet) failed to appear — reported by the PROVIDER, at least
    /// 30 minutes after the booking's scheduled start (terminal, frees capacity).
    /// Also set automatically by the scheduled external job when the provider's
    /// WORKING DAY ends while the booking still sits in <see cref="StartJob"/>:
    /// the provider was there and had the start code issued, but the parent never
    /// handed it back.
    /// </summary>
    public const string ParentNoShow = "PARENT_NO_SHOW";

    /// <summary>
    /// The provider failed to appear — reported by the PARENT, at least
    /// 30 minutes after the booking's scheduled start (terminal, frees capacity).
    /// Also set automatically by the scheduled external job when the provider's
    /// WORKING DAY ends while the booking is still confirmed-equivalent: they
    /// never so much as tapped Start. Keyed off closing time rather than the
    /// booking's own end time, so a provider running late still has their day.
    /// </summary>
    public const string ProviderNoShow = "PROVIDER_NO_SHOW";

    /// <summary>
    /// The booking sat in CREATED for 24+ hours without the provider
    /// accepting. Written ONLY by the scheduled external job — never settable by
    /// a client, and (since 2026-08-02) never written by a sproc: the status
    /// engine rejects a transition attempted on a stale CREATED booking without
    /// flipping it, so a row can still read CREATED while the API already treats
    /// it as expired. Terminal, frees capacity.
    /// </summary>
    public const string Expired = "EXPIRED";

    /// <summary>
    /// <b>Legacy.</b> The provider accepted the booking but the job never got
    /// underway — its scheduled window fully elapsed while the booking was still
    /// confirmed-equivalent or sitting in START_JOB (never reached
    /// <see cref="InProgress"/>). That situation is now settled as a no-show
    /// instead (<see cref="ProviderNoShow"/> / <see cref="ParentNoShow"/>,
    /// depending on whether the provider ever tapped Start). Nothing produces
    /// this value any more for either booking kind.
    /// Kept because existing rows carry it, and it stays terminal + capacity-freeing.
    /// Distinct from <see cref="Expired"/>, which is a CREATED booking the provider
    /// never accepted.
    /// </summary>
    public const string JobExpired = "JOB_EXPIRED";

    /// <summary>
    /// The provider entered the wrong start-OTP too many times (the 6th failed
    /// attempt) — the job is cancelled. Set by the verify sprocs, never by a
    /// client. Terminal, frees capacity. A dedicated status rather than a plain
    /// cancellation so both apps can label it "OTP Max Attempts Exceeded"
    /// instead of guessing at the reason.
    /// </summary>
    public const string OtpMaxAttemptsExceeded = "OTP_MAX_ATTEMPTS_EXCEEDED";

    /// <summary>Every valid status value (APPROVAL_NEEDED + JOB_STARTED kept for legacy rows).</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Created, Confirmed, ProviderDeclined, StartJob, InProgress, Ending, JobStarted,
        Completed, Paid, ApprovalNeeded,
        ModificationRequestByParent, ModificationRequestByProvider,
        ProviderAcceptedModification, ProviderDeclinedModification,
        ParentAcceptedModification, ParentDeclinedModification,
        ProviderCancelled, ParentCancelled,
        ParentNoShow, ProviderNoShow, Expired, JobExpired, OtpMaxAttemptsExceeded
    };

    /// <summary>
    /// "Live" resting states a job can be started, modified, or cancelled from —
    /// CONFIRMED plus the four post-modification resting states. The provider taps
    /// "Start Job" (→ START_JOB) from one of these.
    /// </summary>
    public static readonly IReadOnlySet<string> ConfirmedEquivalent = new HashSet<string>(StringComparer.Ordinal)
    {
        Confirmed, ProviderAcceptedModification, ParentAcceptedModification,
        ProviderDeclinedModification, ParentDeclinedModification
    };

    /// <summary>
    /// States a no-show can be reported from: the confirmed-equivalent resting
    /// states PLUS START_JOB (the provider tapped start but the counterparty never
    /// turned up / never handed over the code). NOT once the job is
    /// <see cref="InProgress"/> — by then both parties met.
    /// </summary>
    public static readonly IReadOnlySet<string> NoShowReportableFrom = new HashSet<string>(StringComparer.Ordinal)
    {
        Confirmed, ProviderAcceptedModification, ParentAcceptedModification,
        ProviderDeclinedModification, ParentDeclinedModification, StartJob
    };

    /// <summary>
    /// The job is actively underway — the provider has entered the start-OTP. A
    /// booking here can no longer be cancelled or reported as a no-show; it runs
    /// through to COMPLETED. (The retired <see cref="Ending"/> is kept so legacy
    /// rows behave the same.)
    /// </summary>
    public static readonly IReadOnlySet<string> JobUnderway = new HashSet<string>(StringComparer.Ordinal)
    {
        InProgress, Ending
    };

    /// <summary>
    /// States a vet may record a prescription from — once the job has started
    /// (IN_PROGRESS; the retired ENDING kept for legacy rows) or after it's done
    /// (COMPLETED).
    /// </summary>
    public static readonly IReadOnlySet<string> PrescriptionAllowedFrom = new HashSet<string>(StringComparer.Ordinal)
    {
        InProgress, Ending, Completed
    };

    /// <summary>
    /// States in which a proposal is sitting in the staging area awaiting the
    /// counterparty's response (so a pending-modification read is worthwhile).
    /// </summary>
    public static readonly IReadOnlySet<string> ModificationRequested = new HashSet<string>(StringComparer.Ordinal)
    {
        ModificationRequestByParent, ModificationRequestByProvider
    };

    /// <summary>
    /// Statuses the provider may set via the simple status engine
    /// (accept / decline / complete / cancel). Start + modifications use their
    /// own dedicated paths.
    /// </summary>
    public static readonly IReadOnlySet<string> ProviderSettable = new HashSet<string>(StringComparer.Ordinal)
    {
        Confirmed, ProviderDeclined, Completed, ProviderCancelled, ParentNoShow
    };

    /// <summary>Statuses the parent may set via the simple status engine.</summary>
    public static readonly IReadOnlySet<string> ParentSettable = new HashSet<string>(StringComparer.Ordinal)
    {
        ParentCancelled, ProviderNoShow
    };

    /// <summary>
    /// Terminal statuses — once a booking reaches one, no further status change
    /// is allowed.
    /// </summary>
    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>(StringComparer.Ordinal)
    {
        Completed, Paid, ProviderDeclined, ProviderCancelled, ParentCancelled,
        ParentNoShow, ProviderNoShow, Expired, JobExpired, OtpMaxAttemptsExceeded
    };

    /// <summary>
    /// The two no-show statuses. A no-show always names the OTHER party: the
    /// provider reports <see cref="ParentNoShow"/>, the parent reports
    /// <see cref="ProviderNoShow"/> — and only 30+ minutes after the booking's
    /// scheduled start.
    /// </summary>
    public static readonly IReadOnlySet<string> NoShow = new HashSet<string>(StringComparer.Ordinal)
    {
        ParentNoShow, ProviderNoShow
    };

    /// <summary>
    /// Statuses that free up capacity. Every other status still holds the
    /// booking's slot — this is the SQL "active booking" predicate.
    /// </summary>
    public static readonly IReadOnlySet<string> Cancelled = new HashSet<string>(StringComparer.Ordinal)
    {
        ProviderCancelled, ParentCancelled, ProviderDeclined,
        ParentNoShow, ProviderNoShow, Expired, JobExpired, OtpMaxAttemptsExceeded
    };

    /// <summary>
    /// Trims + validates an incoming status string. Throws
    /// <see cref="UnsupportedBookingStatusException"/> when it is not one of the
    /// six canonical values.
    /// </summary>
    public static string Normalize(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !All.Contains(trimmed))
        {
            throw new UnsupportedBookingStatusException(value ?? string.Empty);
        }

        return trimmed;
    }
}
