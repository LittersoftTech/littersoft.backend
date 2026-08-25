using Microsoft.Data.SqlClient;
using Pawfront.Application.Bookings;

namespace Pawfront.Infrastructure.Sql.Bookings;

/// <summary>
/// Binds a <see cref="CapturedLocation"/> onto the four location parameters every
/// geolocation-capturing procedure takes. One helper rather than twelve copies of
/// the same four <c>AddWithValue</c> lines, so the null handling (and the parameter
/// NAMES, which must match the procedures exactly) cannot drift between them.
/// </summary>
internal static class LocationParameters
{
    /// <summary>
    /// Adds <c>@Latitude</c>, <c>@Longitude</c>, <c>@AccuracyMetres</c> and
    /// <c>@DeviceCapturedAtUtc</c>. A null <paramref name="location"/> binds all
    /// four as NULL, which the procedures read as "no fix supplied" and skip the
    /// insert for — the case that keeps them callable by paths (accept, decline,
    /// cancel) that capture nothing.
    /// </summary>
    public static void Add(SqlCommand command, CapturedLocation? location)
    {
        command.Parameters.AddWithValue(
            "@Latitude", location is null ? DBNull.Value : location.Latitude);
        command.Parameters.AddWithValue(
            "@Longitude", location is null ? DBNull.Value : location.Longitude);
        command.Parameters.AddWithValue(
            "@AccuracyMetres",
            location?.AccuracyMetres is null ? DBNull.Value : location.AccuracyMetres.Value);
        command.Parameters.AddWithValue(
            "@DeviceCapturedAtUtc",
            location?.DeviceCapturedAtUtc is null
                ? DBNull.Value
                // The column is DATETIME2, so hand over the UTC wall clock rather
                // than the offset-bearing value — everything in this codebase is UTC.
                : location.DeviceCapturedAtUtc.Value.UtcDateTime);
    }
}
