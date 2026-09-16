using System.Globalization;
using System.Security.Cryptography;
using Pawfront.Application.ProviderOnboarding;
using Pawfront.Contracts.ProviderOnboarding;

namespace Pawfront.Infrastructure.Sql.ProviderOnboarding;

internal sealed class InMemoryProviderOnboardingService(IProviderMobileOtpSender otpSender) : IProviderOnboardingService
{
    private readonly object syncRoot = new();
    private readonly Dictionary<Guid, ProviderAuthIdentityState> authIdentities = new();
    private readonly Dictionary<string, Guid> authIdentityIdsByFirebaseUserId = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ProviderProfileState> profilesByProviderId = new();
    private readonly Dictionary<Guid, ProviderMobileOtpState> mobileOtpsById = new();
    private readonly Dictionary<string, ProviderDeviceTokenState> deviceTokensByFcmToken = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> providerIdsByMobileNumber = new(StringComparer.OrdinalIgnoreCase);

    public Task<ProviderFirebaseAuthResponse> SaveFirebaseAuthAsync(
        SaveProviderFirebaseAuthCommand command,
        CancellationToken cancellationToken)
    {
        var authProvider = NormalizeAuthProvider(command.AuthProvider);
        var firebaseUserId = Required(command.FirebaseUserId, nameof(command.FirebaseUserId));
        var email = Required(command.Email, nameof(command.Email));
        var now = DateTimeOffset.UtcNow;

        lock (syncRoot)
        {
            if (authIdentityIdsByFirebaseUserId.TryGetValue(firebaseUserId, out var existingId))
            {
                var existing = authIdentities[existingId];
                existing.AuthProvider = authProvider;
                existing.FirebaseProviderId = TrimOrNull(command.FirebaseProviderId);
                existing.Email = email;
                existing.IsEmailVerified = command.IsEmailVerified;
                existing.DisplayName = TrimOrNull(command.DisplayName);
                existing.FirebasePhoneNumber = TrimOrNull(command.FirebasePhoneNumber);
                existing.PhotoUrl = TrimOrNull(command.PhotoUrl);
                existing.FirebaseTenantId = TrimOrNull(command.FirebaseTenantId);
                existing.LastSignedInAtUtc = now;
                existing.UpdatedAtUtc = now;
                UpsertDeviceToken(existing, command, now);

                return Task.FromResult(ToResponse(existing));
            }

            var identity = new ProviderAuthIdentityState
            {
                ProviderAuthIdentityId = Guid.NewGuid(),
                FirebaseUserId = firebaseUserId,
                AuthProvider = authProvider,
                FirebaseProviderId = TrimOrNull(command.FirebaseProviderId),
                Email = email,
                IsEmailVerified = command.IsEmailVerified,
                DisplayName = TrimOrNull(command.DisplayName),
                FirebasePhoneNumber = TrimOrNull(command.FirebasePhoneNumber),
                PhotoUrl = TrimOrNull(command.PhotoUrl),
                FirebaseTenantId = TrimOrNull(command.FirebaseTenantId),
                SignUpStatus = "FirebaseAuthenticated",
                LastSignedInAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            authIdentities[identity.ProviderAuthIdentityId] = identity;
            authIdentityIdsByFirebaseUserId[identity.FirebaseUserId] = identity.ProviderAuthIdentityId;
            UpsertDeviceToken(identity, command, now);

            return Task.FromResult(ToResponse(identity));
        }
    }

    public Task<ProviderProfileResponse> CompleteProviderProfileAsync(
        CompleteProviderProfileRequest request,
        CancellationToken cancellationToken)
    {
        var firstName = Required(request.FirstName, nameof(request.FirstName));
        var lastName = Required(request.LastName, nameof(request.LastName));
        var gender = NormalizeGender(request.Gender);
        var mobileCountryCode = Required(request.MobileCountryCode, nameof(request.MobileCountryCode));
        var mobileNumber = Required(request.MobileNumber, nameof(request.MobileNumber));
        var mobileKey = $"{mobileCountryCode}:{mobileNumber}";
        var now = DateTimeOffset.UtcNow;

        lock (syncRoot)
        {
            if (!authIdentities.TryGetValue(request.ProviderAuthIdentityId, out var authIdentity))
            {
                throw new ProviderAuthIdentityNotFoundException(request.ProviderAuthIdentityId);
            }

            if (authIdentity.ProviderId is { } existingProviderId)
            {
                return Task.FromResult(ToResponse(profilesByProviderId[existingProviderId]));
            }

            if (providerIdsByMobileNumber.ContainsKey(mobileKey))
            {
                throw new MobileNumberAlreadyExistsException(mobileCountryCode, mobileNumber);
            }

            var profile = new ProviderProfileState
            {
                ProviderId = Guid.NewGuid(),
                ProviderAuthIdentityId = request.ProviderAuthIdentityId,
                FirstName = firstName,
                LastName = lastName,
                Gender = gender,
                MobileCountryCode = mobileCountryCode,
                MobileNumber = mobileNumber,
                DateOfBirth = request.DateOfBirth,
                OnboardingStatus = "MobileVerificationPending",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            profilesByProviderId[profile.ProviderId] = profile;
            providerIdsByMobileNumber[mobileKey] = profile.ProviderId;
            authIdentity.ProviderId = profile.ProviderId;
            authIdentity.SignUpStatus = "ProviderProfileCompleted";
            authIdentity.UpdatedAtUtc = now;

            foreach (var deviceToken in deviceTokensByFcmToken.Values
                         .Where(deviceToken => deviceToken.ProviderAuthIdentityId == request.ProviderAuthIdentityId))
            {
                deviceToken.ProviderId = profile.ProviderId;
                deviceToken.UpdatedAtUtc = now;
            }

            return Task.FromResult(ToResponse(profile));
        }
    }

    public Task<ProviderProfileResponse> GetProviderProfileAsync(
        Guid providerId,
        CancellationToken cancellationToken)
    {
        lock (syncRoot)
        {
            if (!profilesByProviderId.TryGetValue(providerId, out var profile))
            {
                throw new ProviderProfileNotFoundException(providerId);
            }
            return Task.FromResult(ToResponse(profile));
        }
    }

    public Task<ProviderProfileResponse> UpdateProviderProfileAsync(
        Guid providerId,
        UpdateProviderProfileRequest request,
        CancellationToken cancellationToken)
    {
        var firstName = Required(request.FirstName, nameof(request.FirstName));
        var lastName = Required(request.LastName, nameof(request.LastName));
        var gender = NormalizeGender(request.Gender);

        lock (syncRoot)
        {
            if (!profilesByProviderId.TryGetValue(providerId, out var profile))
            {
                throw new ProviderProfileNotFoundException(providerId);
            }

            profile.FirstName = firstName;
            profile.LastName = lastName;
            profile.Gender = gender;
            profile.DateOfBirth = request.DateOfBirth;
            profile.UpdatedAtUtc = DateTimeOffset.UtcNow;

            return Task.FromResult(ToResponse(profile));
        }
    }

    public Task<ProviderAccountDeletionResult> DeleteProviderAccountAsync(
        Guid providerId,
        CancellationToken cancellationToken)
    {
        // Dev fallback only. Mirrors the SQL anonymise + disable for the state this
        // service owns; bookings, events, services and policies live in their own
        // in-memory stores it can't reach, so the retained counts are reported as 0.
        // Production uses Provider.DeleteProvider.
        lock (syncRoot)
        {
            if (!profilesByProviderId.TryGetValue(providerId, out var profile))
            {
                throw new ProviderProfileNotFoundException(providerId);
            }

            var identity = authIdentities[profile.ProviderAuthIdentityId];

            if (profile.IsDeleted)
            {
                return Task.FromResult(new ProviderAccountDeletionResult(
                    BuildDeleteSummary(providerId, profile.DeletedAtUtc ?? DateTimeOffset.UtcNow, wasAlreadyDeleted: true),
                    ServiceCategories: [],
                    BlobUrls: []));
            }

            var now = DateTimeOffset.UtcNow;
            var bannerImageUrl = profile.BannerImageUrl;

            // Free the real number + Firebase uid so the person can register again.
            providerIdsByMobileNumber.Remove($"{profile.MobileCountryCode}:{profile.MobileNumber}");
            authIdentityIdsByFirebaseUserId.Remove(identity.FirebaseUserId);

            profile.FirstName = "Deleted";
            profile.LastName = "Provider";
            profile.Gender = "PreferNotToSay";
            profile.DateOfBirth = new DateOnly(1900, 1, 1);
            profile.MobileVerifiedAtUtc = null;
            profile.BannerImageUrl = null;
            profile.IsActive = false;
            profile.IsDeleted = true;
            profile.DeletedAtUtc = now;
            profile.UpdatedAtUtc = now;

            identity.FirebaseUserId = $"deleted:{providerId}";
            identity.Email = $"deleted+{providerId}@deleted.invalid";
            identity.IsEmailVerified = false;
            identity.DisplayName = null;
            identity.FirebasePhoneNumber = null;
            identity.PhotoUrl = null;
            identity.FirebaseTenantId = null;
            identity.UpdatedAtUtc = now;
            authIdentityIdsByFirebaseUserId[identity.FirebaseUserId] = identity.ProviderAuthIdentityId;

            foreach (var otpId in mobileOtpsById
                         .Where(entry => entry.Value.ProviderId == providerId)
                         .Select(entry => entry.Key)
                         .ToList())
            {
                mobileOtpsById.Remove(otpId);
            }

            foreach (var fcmToken in deviceTokensByFcmToken
                         .Where(entry => entry.Value.ProviderId == providerId
                                         || entry.Value.ProviderAuthIdentityId == identity.ProviderAuthIdentityId)
                         .Select(entry => entry.Key)
                         .ToList())
            {
                deviceTokensByFcmToken.Remove(fcmToken);
            }

            return Task.FromResult(new ProviderAccountDeletionResult(
                BuildDeleteSummary(providerId, now, wasAlreadyDeleted: false),
                ServiceCategories: [],
                BlobUrls: bannerImageUrl is null ? [] : [bannerImageUrl]));
        }
    }

    private static DeleteProviderAccountResponse BuildDeleteSummary(
        Guid providerId,
        DateTimeOffset deletedAtUtc,
        bool wasAlreadyDeleted)
    {
        return new DeleteProviderAccountResponse(
            providerId,
            deletedAtUtc,
            wasAlreadyDeleted,
            DeactivatedServiceCount: 0,
            RetainedBookingCount: 0,
            RetainedNightStayBookingCount: 0,
            RetainedEventCount: 0,
            RetainedPaymentCount: 0);
    }

    public Task<ResolveProviderByFirebaseUidResponse> ResolveProviderByFirebaseUidAsync(
        string firebaseUserId,
        CancellationToken cancellationToken)
    {
        var normalized = Required(firebaseUserId, nameof(firebaseUserId));

        lock (syncRoot)
        {
            if (!authIdentityIdsByFirebaseUserId.TryGetValue(normalized, out var identityId))
            {
                throw new ProviderAuthIdentityForFirebaseUserNotFoundException(normalized);
            }

            var identity = authIdentities[identityId];
            ProviderProfileState? profile = null;
            if (identity.ProviderId is { } providerId
                && profilesByProviderId.TryGetValue(providerId, out var foundProfile))
            {
                profile = foundProfile;
            }

            return Task.FromResult(new ResolveProviderByFirebaseUidResponse(
                identity.ProviderAuthIdentityId,
                identity.ProviderId,
                identity.FirebaseUserId,
                identity.Email,
                identity.IsEmailVerified,
                identity.DisplayName,
                identity.SignUpStatus,
                profile is not null,
                profile?.OnboardingStatus,
                profile?.MobileVerifiedAtUtc,
                profile?.IsActive));
        }
    }

    public Task<SetActiveStatusOutcome> SetActiveStatusAsync(
        Guid providerId,
        bool isActive,
        bool acknowledgeExistingBookings,
        CancellationToken cancellationToken)
    {
        // Dev fallback only. Bookings live in InMemoryBookingStore (not reachable
        // from this service), so the conflict check is skipped here — which also
        // means acknowledgeExistingBookings has nothing to acknowledge and the
        // honoured count is always 0. Production uses the SQL path, which
        // enforces both inside Provider.SetProviderActiveStatus.
        var now = DateTimeOffset.UtcNow;

        lock (syncRoot)
        {
            if (!profilesByProviderId.TryGetValue(providerId, out var profile))
            {
                throw new ProviderProfileNotFoundException(providerId);
            }

            profile.IsActive = isActive;
            profile.UpdatedAtUtc = now;

            return Task.FromResult<SetActiveStatusOutcome>(
                new SetActiveStatusOutcome.Updated(providerId, isActive, now, HonouredBookingCount: 0));
        }
    }

    public async Task<SendProviderMobileOtpResponse> SendProviderMobileOtpAsync(
        Guid providerId,
        CancellationToken cancellationToken)
    {
        string otpCode;
        SendProviderMobileOtpResponse response;

        lock (syncRoot)
        {
            if (!profilesByProviderId.TryGetValue(providerId, out var profile))
            {
                throw new ProviderProfileNotFoundException(providerId);
            }

            otpCode = GenerateOtpCode();
            var now = DateTimeOffset.UtcNow;
            var otp = new ProviderMobileOtpState
            {
                ProviderMobileOtpId = Guid.NewGuid(),
                ProviderId = providerId,
                MobileCountryCode = profile.MobileCountryCode,
                MobileNumber = profile.MobileNumber,
                OtpCode = otpCode,
                ValidationStatus = "Pending",
                DateSentUtc = now,
                ExpiresAtUtc = now.AddMinutes(10),
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            mobileOtpsById[otp.ProviderMobileOtpId] = otp;
            response = ToResponse(otp);
        }

        await otpSender.SendMobileOtpAsync(
            response.ProviderId,
            response.MobileCountryCode,
            response.MobileNumber,
            otpCode,
            cancellationToken);

        return response;
    }

    public Task<VerifyProviderMobileOtpResponse> VerifyProviderMobileOtpAsync(
        Guid providerId,
        Guid providerMobileOtpId,
        VerifyProviderMobileOtpRequest request,
        CancellationToken cancellationToken)
    {
        var otpCode = Required(request.OtpCode, nameof(request.OtpCode));
        var now = DateTimeOffset.UtcNow;

        lock (syncRoot)
        {
            if (!mobileOtpsById.TryGetValue(providerMobileOtpId, out var otp) || otp.ProviderId != providerId)
            {
                throw new ProviderMobileOtpNotFoundException(providerMobileOtpId);
            }

            if (otp.ValidationStatus == "Validated")
            {
                return Task.FromResult(ToVerificationResponse(otp, true, "Validated"));
            }

            if (otp.ValidationStatus == "Expired" || now >= otp.ExpiresAtUtc)
            {
                otp.ValidationStatus = "Expired";
                otp.UpdatedAtUtc = now;
                return Task.FromResult(ToVerificationResponse(otp, false, "Expired"));
            }

            if (!string.Equals(otp.OtpCode, otpCode, StringComparison.Ordinal))
            {
                otp.FailedAttemptCount++;
                otp.UpdatedAtUtc = now;
                return Task.FromResult(ToVerificationResponse(otp, false, "Invalid"));
            }

            otp.ValidationStatus = "Validated";
            otp.DateValidatedUtc = now;
            otp.UpdatedAtUtc = now;

            var profile = profilesByProviderId[providerId];
            profile.MobileVerifiedAtUtc ??= now;
            profile.OnboardingStatus = "MobileVerified";
            profile.UpdatedAtUtc = now;

            return Task.FromResult(ToVerificationResponse(otp, true, "Validated"));
        }
    }

    private static ProviderFirebaseAuthResponse ToResponse(ProviderAuthIdentityState identity)
    {
        return new ProviderFirebaseAuthResponse(
            identity.ProviderAuthIdentityId,
            identity.ProviderId,
            identity.FirebaseUserId,
            identity.AuthProvider,
            identity.FirebaseProviderId,
            identity.Email,
            identity.IsEmailVerified,
            identity.DisplayName,
            identity.FirebasePhoneNumber,
            identity.PhotoUrl,
            identity.FirebaseTenantId,
            identity.SignUpStatus,
            identity.LastSignedInAtUtc,
            identity.CreatedAtUtc,
            identity.UpdatedAtUtc);
    }

    private static ProviderProfileResponse ToResponse(ProviderProfileState profile)
    {
        return new ProviderProfileResponse(
            profile.ProviderId,
            profile.ProviderAuthIdentityId,
            profile.FirstName,
            profile.LastName,
            profile.Gender,
            profile.MobileCountryCode,
            profile.MobileNumber,
            profile.DateOfBirth,
            profile.MobileVerifiedAtUtc,
            profile.OnboardingStatus,
            profile.IsActive,
            profile.CreatedAtUtc,
            profile.UpdatedAtUtc,
            profile.BannerImageUrl);
    }

    private static SendProviderMobileOtpResponse ToResponse(ProviderMobileOtpState otp)
    {
        return new SendProviderMobileOtpResponse(
            otp.ProviderMobileOtpId,
            otp.ProviderId,
            otp.MobileCountryCode,
            otp.MobileNumber,
            otp.DateSentUtc,
            otp.ExpiresAtUtc);
    }

    private static VerifyProviderMobileOtpResponse ToVerificationResponse(
        ProviderMobileOtpState otp,
        bool isValidated,
        string validationStatus)
    {
        return new VerifyProviderMobileOtpResponse(
            otp.ProviderMobileOtpId,
            otp.ProviderId,
            isValidated,
            validationStatus,
            otp.DateSentUtc,
            otp.DateValidatedUtc,
            otp.ExpiresAtUtc);
    }

    private static string GenerateOtpCode()
    {
        return RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static string NormalizeAuthProvider(string? value)
    {
        return Required(value, nameof(value)) switch
        {
            "Google" => "Google",
            "Apple" => "Apple",
            "EmailPassword" => "EmailPassword",
            var unsupported => throw new UnsupportedAuthProviderException(unsupported)
        };
    }

    private static string NormalizeGender(string? value)
    {
        return Required(value, nameof(value)) switch
        {
            "Male" => "Male",
            "Female" => "Female",
            "NonBinary" => "NonBinary",
            "Other" => "Other",
            "PreferNotToSay" => "PreferNotToSay",
            var unsupported => throw new UnsupportedGenderException(unsupported)
        };
    }

    private void UpsertDeviceToken(
        ProviderAuthIdentityState authIdentity,
        SaveProviderFirebaseAuthCommand command,
        DateTimeOffset now)
    {
        var fcmToken = TrimOrNull(command.FcmToken);
        if (fcmToken is null)
        {
            return;
        }

        if (deviceTokensByFcmToken.TryGetValue(fcmToken, out var existing))
        {
            existing.ProviderAuthIdentityId = authIdentity.ProviderAuthIdentityId;
            existing.ProviderId = authIdentity.ProviderId;
            existing.DeviceId = TrimOrNull(command.DeviceId);
            existing.DevicePlatform = NormalizeDevicePlatformOrNull(command.DevicePlatform);
            existing.IsActive = true;
            existing.LastSeenAtUtc = now;
            existing.UpdatedAtUtc = now;
            return;
        }

        deviceTokensByFcmToken[fcmToken] = new ProviderDeviceTokenState
        {
            ProviderDeviceTokenId = Guid.NewGuid(),
            ProviderAuthIdentityId = authIdentity.ProviderAuthIdentityId,
            ProviderId = authIdentity.ProviderId,
            FcmToken = fcmToken,
            DeviceId = TrimOrNull(command.DeviceId),
            DevicePlatform = NormalizeDevicePlatformOrNull(command.DevicePlatform),
            IsActive = true,
            LastSeenAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }

    private static string? NormalizeDevicePlatformOrNull(string? value)
    {
        var trimmed = TrimOrNull(value);
        return trimmed switch
        {
            null => null,
            "Android" => "Android",
            "iOS" => "iOS",
            var unsupported => throw new ArgumentException($"Device platform '{unsupported}' is not supported.", nameof(value))
        };
    }

    private static string Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", name);
        }

        return value.Trim();
    }

    private static string? TrimOrNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private sealed class ProviderAuthIdentityState
    {
        public Guid ProviderAuthIdentityId { get; init; }
        public Guid? ProviderId { get; set; }
        // Settable: the account delete replaces it with a placeholder so the real
        // Firebase uid is free for a fresh sign-up.
        public required string FirebaseUserId { get; set; }
        public string? FirebaseTenantId { get; set; }
        public required string AuthProvider { get; set; }
        public string? FirebaseProviderId { get; set; }
        public required string Email { get; set; }
        public bool IsEmailVerified { get; set; }
        public string? DisplayName { get; set; }
        public string? FirebasePhoneNumber { get; set; }
        public string? PhotoUrl { get; set; }
        public required string SignUpStatus { get; set; }
        public DateTimeOffset LastSignedInAtUtc { get; set; }
        public DateTimeOffset CreatedAtUtc { get; init; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }

    private sealed class ProviderProfileState
    {
        public Guid ProviderId { get; init; }
        public Guid ProviderAuthIdentityId { get; init; }
        // Editable via UpdateProviderProfileAsync.
        public required string FirstName { get; set; }
        public required string LastName { get; set; }
        public required string Gender { get; set; }
        public required string MobileCountryCode { get; init; }
        public required string MobileNumber { get; init; }
        public DateOnly DateOfBirth { get; set; }
        public DateTimeOffset? MobileVerifiedAtUtc { get; set; }
        public required string OnboardingStatus { get; set; }
        public bool IsActive { get; set; } = true;
        public bool IsDeleted { get; set; }
        public DateTimeOffset? DeletedAtUtc { get; set; }
        public string? BannerImageUrl { get; set; }
        public DateTimeOffset CreatedAtUtc { get; init; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }

    private sealed class ProviderMobileOtpState
    {
        public Guid ProviderMobileOtpId { get; init; }
        public Guid ProviderId { get; init; }
        public required string MobileCountryCode { get; init; }
        public required string MobileNumber { get; init; }
        public required string OtpCode { get; init; }
        public required string ValidationStatus { get; set; }
        public int FailedAttemptCount { get; set; }
        public DateTimeOffset DateSentUtc { get; init; }
        public DateTimeOffset? DateValidatedUtc { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; init; }
        public DateTimeOffset CreatedAtUtc { get; init; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }

    private sealed class ProviderDeviceTokenState
    {
        public Guid ProviderDeviceTokenId { get; init; }
        public Guid ProviderAuthIdentityId { get; set; }
        public Guid? ProviderId { get; set; }
        public required string FcmToken { get; init; }
        public string? DeviceId { get; set; }
        public string? DevicePlatform { get; set; }
        public bool IsActive { get; set; }
        public DateTimeOffset LastSeenAtUtc { get; set; }
        public DateTimeOffset CreatedAtUtc { get; init; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }
}
