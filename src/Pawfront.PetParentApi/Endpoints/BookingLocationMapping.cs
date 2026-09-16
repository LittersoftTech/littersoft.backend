using Pawfront.Application.Bookings;
using Pawfront.Contracts.Bookings;

namespace Pawfront.PetParentApi.Endpoints;

/// <summary>
/// Wire ↔ Application mapping for the booking geolocation captures. Duplicated on
/// the provider host, matching how the review and support-ticket mappings are
/// duplicated: the hosts share the Application layer, not their endpoint layers.
/// </summary>
internal static class BookingLocationMapping
{
    /// <summary>
    /// Maps the request block, or null when the client sent no usable pair. Null is
    /// deliberately returned rather than thrown here so the Application layer raises
    /// the typed <see cref="MissingCapturedLocationException"/> — one message, one
    /// error code, whichever route the fix arrived on.
    /// </summary>
    public static CapturedLocation? ToCapturedLocation(this CapturedLocationRequest? request)
        => request is { Latitude: not null, Longitude: not null }
            ? new CapturedLocation(
                request.Latitude.Value,
                request.Longitude.Value,
                request.AccuracyMetres,
                request.CapturedAtUtc)
            : null;

    /// <summary>Builds a fix from the flat form fields of a multipart upload.</summary>
    public static CapturedLocation? ToCapturedLocation(
        decimal? latitude, decimal? longitude, decimal? accuracyMetres, DateTimeOffset? capturedAtUtc)
        => latitude is not null && longitude is not null
            ? new CapturedLocation(latitude.Value, longitude.Value, accuracyMetres, capturedAtUtc)
            : null;

    public static BookingLocationEventResponse ToResponse(BookingLocationEventResult result) => new(
        result.BookingLocationEventId,
        result.BookingId,
        result.Trigger,
        result.CapturedByType,
        result.CapturedById,
        result.Latitude,
        result.Longitude,
        result.AccuracyMetres,
        result.DeviceCapturedAtUtc,
        result.RecordedAtUtc);
}
