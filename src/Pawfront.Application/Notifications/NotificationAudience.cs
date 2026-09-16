namespace Pawfront.Application.Notifications;

/// <summary>
/// Which app a notification is destined for. This is more than a label: the two
/// apps authenticate against DIFFERENT Firebase projects
/// (<c>littersoftprovider</c> and <c>pawfrontparent-89296</c>), so the audience
/// selects both the device-token table to read and the Firebase credential to
/// send with.
/// </summary>
public enum NotificationAudience
{
    /// <summary>Provider app — tokens in <c>Provider.ProviderDeviceTokens</c>, keyed by ProviderId.</summary>
    Provider,

    /// <summary>Pet-parent app — tokens in <c>Parent.ParentDeviceTokens</c>, keyed by PetParentId.</summary>
    PetParent
}

public static class NotificationAudiences
{
    /// <summary>
    /// The literal stored in <c>Notification.NotificationOutbox.Audience</c> and
    /// checked by <c>CK_NotificationOutbox_Audience</c>. Kept explicit rather than
    /// relying on <see cref="Enum.ToString()"/> so renaming the enum member can
    /// never silently change what is written to the database.
    /// </summary>
    public static string ToSqlValue(this NotificationAudience audience) => audience switch
    {
        NotificationAudience.Provider => "Provider",
        NotificationAudience.PetParent => "PetParent",
        _ => throw new ArgumentOutOfRangeException(nameof(audience), audience, "Unknown notification audience.")
    };

    public static NotificationAudience FromSqlValue(string value) => value switch
    {
        "Provider" => NotificationAudience.Provider,
        "PetParent" => NotificationAudience.PetParent,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown notification audience.")
    };
}
