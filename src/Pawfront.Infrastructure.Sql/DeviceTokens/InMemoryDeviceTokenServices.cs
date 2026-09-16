using System.Collections.Concurrent;
using Pawfront.Application.DeviceTokens;
using Pawfront.Contracts.DeviceTokens;

namespace Pawfront.Infrastructure.Sql.DeviceTokens;

/// <summary>
/// Dev fallback used when no SQL connection string is configured. Keeps tokens in
/// a dictionary so the endpoints behave correctly end-to-end locally, including
/// the same-device retirement rule — matching the other in-memory stores in this
/// project rather than throwing or silently no-oping.
///
/// Notifications themselves still go nowhere on this path
/// (<c>NullNotificationPublisher</c>), so these tokens are never actually pushed to.
/// </summary>
internal sealed class InMemoryDeviceTokenStore
{
    private sealed record Entry(
        Guid DeviceTokenId,
        string FirebaseUserId,
        string FcmToken,
        string? DeviceId,
        string? DevicePlatform,
        bool IsActive,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc);

    private readonly ConcurrentDictionary<string, Entry> _byToken = new(StringComparer.Ordinal);

    public DeviceTokenResponse Register(string firebaseUserId, RegisterDeviceTokenRequest request)
    {
        var fcmToken = Require(request.FcmToken, nameof(request.FcmToken));
        var platform = DevicePlatforms.Normalize(request.DevicePlatform);
        var deviceId = string.IsNullOrWhiteSpace(request.DeviceId) ? null : request.DeviceId.Trim();
        var now = DateTimeOffset.UtcNow;

        var retired = 0;
        if (deviceId is not null)
        {
            foreach (var (key, entry) in _byToken)
            {
                if (entry.IsActive
                    && string.Equals(entry.DeviceId, deviceId, StringComparison.Ordinal)
                    && !string.Equals(key, fcmToken, StringComparison.Ordinal))
                {
                    _byToken[key] = entry with { IsActive = false, UpdatedAtUtc = now };
                    retired++;
                }
            }
        }

        var saved = _byToken.AddOrUpdate(
            fcmToken,
            _ => new Entry(Guid.NewGuid(), firebaseUserId, fcmToken, deviceId, platform, true, now, now),
            (_, existing) => existing with
            {
                FirebaseUserId = firebaseUserId,
                DeviceId = deviceId ?? existing.DeviceId,
                DevicePlatform = platform ?? existing.DevicePlatform,
                IsActive = true,
                UpdatedAtUtc = now
            });

        return ToResponse(saved, retired);
    }

    public DeviceTokenResponse Deactivate(string firebaseUserId, string fcmToken)
    {
        var token = Require(fcmToken, nameof(fcmToken));

        if (!_byToken.TryGetValue(token, out var existing)
            || !string.Equals(existing.FirebaseUserId, firebaseUserId, StringComparison.Ordinal))
        {
            // Unknown and not-yours are one case, same as the sprocs.
            throw new DeviceTokenNotFoundException();
        }

        var updated = existing with { IsActive = false, UpdatedAtUtc = DateTimeOffset.UtcNow };
        _byToken[token] = updated;

        return ToResponse(updated, 0);
    }

    private static DeviceTokenResponse ToResponse(Entry entry, int retired) =>
        new(
            entry.DeviceTokenId,
            // No profile rows exist on this path, so there is no owner id to report.
            null,
            entry.DeviceId,
            entry.DevicePlatform,
            entry.IsActive,
            retired,
            entry.UpdatedAtUtc,
            entry.CreatedAtUtc,
            entry.UpdatedAtUtc);

    private static string Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", name);
        }

        return value.Trim();
    }
}

internal sealed class InMemoryProviderDeviceTokenService(InMemoryDeviceTokenStore store)
    : IProviderDeviceTokenService
{
    public Task<DeviceTokenResponse> RegisterAsync(
        string firebaseUserId, RegisterDeviceTokenRequest request, CancellationToken cancellationToken)
        => Task.FromResult(store.Register(firebaseUserId, request));

    public Task<DeviceTokenResponse> DeactivateAsync(
        string firebaseUserId, string fcmToken, CancellationToken cancellationToken)
        => Task.FromResult(store.Deactivate(firebaseUserId, fcmToken));
}

internal sealed class InMemoryPetParentDeviceTokenService(InMemoryDeviceTokenStore store)
    : IPetParentDeviceTokenService
{
    public Task<DeviceTokenResponse> RegisterAsync(
        string firebaseUserId, RegisterDeviceTokenRequest request, CancellationToken cancellationToken)
        => Task.FromResult(store.Register(firebaseUserId, request));

    public Task<DeviceTokenResponse> DeactivateAsync(
        string firebaseUserId, string fcmToken, CancellationToken cancellationToken)
        => Task.FromResult(store.Deactivate(firebaseUserId, fcmToken));
}
