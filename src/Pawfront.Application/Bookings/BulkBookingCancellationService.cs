namespace Pawfront.Application.Bookings;

/// <inheritdoc cref="IBulkBookingCancellationService"/>
public sealed class BulkBookingCancellationService(
    IBookingService bookingService,
    INightStayBookingService nightStayBookingService) : IBulkBookingCancellationService
{
    public async Task<BulkCancelBookingsResult> CancelAsync(
        BulkCancelBookingsCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var items = command.Items ?? Array.Empty<BulkCancelBookingItem>();
        if (items.Count == 0)
        {
            throw new ArgumentException("At least one booking is required.", nameof(command));
        }

        if (items.Count > BulkBookingCancellationLimits.MaxItems)
        {
            throw new ArgumentException(
                $"A batch can hold at most {BulkBookingCancellationLimits.MaxItems} bookings; {items.Count} were sent.",
                nameof(command));
        }

        // The actor decides the status, exactly as the single-booking endpoints
        // pin it — a cancellation always names who did it.
        var targetStatus = command.Actor == BookingStatusActor.Provider
            ? BookingStatuses.ProviderCancelled
            : BookingStatuses.ParentCancelled;

        var outcomes = new List<BulkCancelBookingOutcome>(items.Count);
        var seen = new HashSet<(Guid BookingId, string BookingType)>();

        // Sequential on purpose. Each transition takes UPDLOCK + HOLDLOCK on its
        // booking and, for one provider's bookings, on overlapping rows — running
        // a batch concurrently would buy little and invite lock contention
        // between items that are usually the same provider's.
        foreach (var item in items)
        {
            var bookingType = BookingTypes.Normalize(item.BookingType);
            if (bookingType is null)
            {
                outcomes.Add(Failed(
                    item.BookingId,
                    item.BookingType ?? string.Empty,
                    BulkCancelErrorCodes.UnsupportedBookingType,
                    $"'{item.BookingType}' is not a booking type. Use '{BookingTypes.SingleDay}' or '{BookingTypes.NightStay}'."));
                continue;
            }

            // A repeated (id, type) names the same booking. Attempting it twice
            // would cancel it once and then report "already cancelled" — a
            // failure the caller never asked for — so collapse it instead.
            if (!seen.Add((item.BookingId, bookingType)))
            {
                continue;
            }

            outcomes.Add(await CancelOneAsync(
                item.BookingId, bookingType, targetStatus, command, cancellationToken));
        }

        var cancelledCount = outcomes.Count(o => o.Cancelled);
        return new BulkCancelBookingsResult(
            outcomes.Count,
            cancelledCount,
            outcomes.Count - cancelledCount,
            outcomes);
    }

    private async Task<BulkCancelBookingOutcome> CancelOneAsync(
        Guid bookingId,
        string bookingType,
        string targetStatus,
        BulkCancelBookingsCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.Equals(bookingType, BookingTypes.NightStay, StringComparison.Ordinal))
            {
                var stay = await nightStayBookingService.UpdateStatusAsync(
                    new UpdateNightStayBookingStatusCommand(
                        bookingId, targetStatus, command.Actor, command.ActorId, command.Note),
                    cancellationToken);

                return Cancelled(bookingId, bookingType, stay.Status, stay.CancelledAtUtc);
            }

            var booking = await bookingService.UpdateStatusAsync(
                new UpdateBookingStatusCommand(
                    bookingId, targetStatus, command.Actor, command.ActorId, command.Note),
                cancellationToken);

            return Cancelled(bookingId, bookingType, booking.Status, booking.CancelledAtUtc);
        }
        catch (Exception exception) when (ResolveFailureCode(exception) is { } code)
        {
            return Failed(bookingId, bookingType, code, exception.Message);
        }
    }

    /// <summary>
    /// The per-item failure code for an exception a cancel transition raised, or
    /// null when the exception is not one of them — in which case it propagates
    /// and fails the request, because an unrecognised fault is a fault, not a
    /// booking the caller may not cancel.
    /// </summary>
    private static string? ResolveFailureCode(Exception exception) => exception switch
    {
        BookingNotFoundException => BulkCancelErrorCodes.BookingNotFound,
        NightStayBookingNotFoundException => BulkCancelErrorCodes.NightStayBookingNotFound,
        BookingStatusForbiddenException => BulkCancelErrorCodes.Forbidden,
        BookingJobInProgressException => BulkCancelErrorCodes.BookingInProgress,
        BookingStatusTerminalException => BulkCancelErrorCodes.BookingStatusTerminal,
        BookingStatusUnchangedException => BulkCancelErrorCodes.BookingStatusUnchanged,
        BookingExpiredException => BulkCancelErrorCodes.BookingExpired,
        BookingStatusNotAllowedException => BulkCancelErrorCodes.BookingStatusNotAllowed,
        UnsupportedBookingStatusException => BulkCancelErrorCodes.UnsupportedBookingStatus,
        ArgumentException => BulkCancelErrorCodes.InvalidRequest,
        _ => null
    };

    private static BulkCancelBookingOutcome Cancelled(
        Guid bookingId, string bookingType, string status, DateTimeOffset? cancelledAtUtc) =>
        new(bookingId, bookingType, true, status, cancelledAtUtc, null, null);

    private static BulkCancelBookingOutcome Failed(
        Guid bookingId, string bookingType, string errorCode, string message) =>
        new(bookingId, bookingType, false, null, null, errorCode, message);
}
