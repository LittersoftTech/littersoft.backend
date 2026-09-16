namespace Pawfront.Application.Jobs;

/// <summary>
/// The provider's job list — the agenda / "jobs to review" inbox behind their
/// filter sheet.
/// </summary>
/// <remarks>
/// <para>
/// Read-only, like <see cref="Earnings.IProviderEarningsService"/> and for the
/// same reason: nothing here changes a booking. Every action on a job still goes
/// through <c>IBookingService</c> / <c>INightStayBookingService</c>, so this adds
/// a way to FIND jobs and not a second way to change them.
/// </para>
/// <para>
/// It is a separate service rather than another method on the earnings service
/// because the two answer different questions: earnings asks what a provider was
/// paid and defaults to the jobs that produced money, whereas this asks what work
/// they have and defaults to all of it. They do share the underlying definition
/// of a job's worth — both read <c>Booking.BookingAmounts</c> — so an amount on
/// this screen and the same amount on the earnings screen cannot drift.
/// </para>
/// </remarks>
public interface IProviderJobService
{
    Task<ProviderJobPage> ListAsync(
        ProviderJobQuery query,
        CancellationToken cancellationToken);
}

/// <summary>
/// Narrow SQL reader behind <see cref="IProviderJobService"/>. The fee percentage
/// is passed in rather than read here: it lives in configuration
/// (<c>Payments:PawfrontFeePercentage</c>) and only the Application layer should
/// know that — the same split <c>IProviderEarningsStore</c> makes.
/// </summary>
public interface IProviderJobStore
{
    Task<ProviderJobPage> ListAsync(
        ProviderJobQuery query,
        decimal feePercentage,
        CancellationToken cancellationToken);
}
