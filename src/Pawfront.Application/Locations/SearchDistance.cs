namespace Pawfront.Application.Locations;

/// <summary>Request-scoped straight-line distance, independent of filtering and paging.</summary>
public sealed record SearchDistance
{
    private readonly double? latitude;
    private readonly double? longitude;
    public string Unit { get; }
    public bool HasOrigin => latitude.HasValue;

    public SearchDistance(double? latitude, double? longitude, string? distanceUnit)
    {
        Unit = distanceUnit is null ? "km" : distanceUnit.Trim().ToLowerInvariant();
        if (Unit is not ("km" or "mi"))
            throw new ArgumentException("distanceUnit must be 'km' or 'mi'.");
        if (latitude.HasValue != longitude.HasValue)
            throw new ArgumentException("latitude and longitude must be supplied together.");
        if (latitude.HasValue && !Valid(latitude.Value, longitude!.Value))
            throw new ArgumentException("latitude must be finite and between -90 and 90; longitude must be finite and between -180 and 180.");
        this.latitude = latitude;
        this.longitude = longitude;
    }

    public double? To(double? destinationLatitude, double? destinationLongitude)
    {
        if (!HasOrigin || destinationLatitude is not { } lat || destinationLongitude is not { } lon || !Valid(lat, lon))
            return null;
        const double radians = Math.PI / 180;
        var dLat = (lat - latitude!.Value) * radians;
        var dLon = (lon - longitude!.Value) * radians;
        var a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(latitude.Value * radians)
            * Math.Cos(lat * radians) * Math.Pow(Math.Sin(dLon / 2), 2);
        var km = 6371.0088 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
        return Math.Round(Unit == "mi" ? km / 1.609344 : km, 1, MidpointRounding.AwayFromZero);
    }

    private static bool Valid(double lat, double lon) =>
        double.IsFinite(lat) && double.IsFinite(lon) && lat >= -90 && lat <= 90 && lon >= -180 && lon <= 180;
}
