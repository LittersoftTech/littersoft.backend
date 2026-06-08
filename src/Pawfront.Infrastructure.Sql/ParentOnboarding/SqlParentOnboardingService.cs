using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.ParentOnboarding;
using Pawfront.Contracts.ParentOnboarding;

namespace Pawfront.Infrastructure.Sql.ParentOnboarding;

internal sealed class SqlParentOnboardingService(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider,
    IPetParentMobileOtpSender otpSender) : IParentOnboardingService
{
    public async Task<ParentFirebaseAuthResponse> SaveFirebaseAuthAsync(
        SaveParentFirebaseAuthCommand commandInput,
        CancellationToken cancellationToken)
    {
        var authProvider = NormalizeAuthProvider(commandInput.AuthProvider);
        var firebaseUserId = Required(commandInput.FirebaseUserId, nameof(commandInput.FirebaseUserId));
        var email = Required(commandInput.Email, nameof(commandInput.Email));

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(
            connection,
            "Parent.SaveParentAuthIdentity");

        command.Parameters.AddWithValue("@FirebaseUserId", firebaseUserId);
        command.Parameters.AddWithValue("@FirebaseTenantId", DbValue(TrimOrNull(commandInput.FirebaseTenantId)));
        command.Parameters.AddWithValue("@AuthProvider", authProvider);
        command.Parameters.AddWithValue("@FirebaseProviderId", DbValue(TrimOrNull(commandInput.FirebaseProviderId)));
        command.Parameters.AddWithValue("@Email", email);
        command.Parameters.AddWithValue("@IsEmailVerified", commandInput.IsEmailVerified);
        command.Parameters.AddWithValue("@DisplayName", DbValue(TrimOrNull(commandInput.DisplayName)));
        command.Parameters.AddWithValue("@FirebasePhoneNumber", DbValue(TrimOrNull(commandInput.FirebasePhoneNumber)));
        command.Parameters.AddWithValue("@PhotoUrl", DbValue(TrimOrNull(commandInput.PhotoUrl)));
        command.Parameters.AddWithValue("@FcmToken", DbValue(TrimOrNull(commandInput.FcmToken)));
        command.Parameters.AddWithValue("@DeviceId", DbValue(TrimOrNull(commandInput.DeviceId)));
        command.Parameters.AddWithValue("@DevicePlatform", DbValue(NormalizeDevicePlatformOrNull(commandInput.DevicePlatform)));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Parent auth identity was not returned after save.");
        }

        return ReadAuthIdentity(reader);
    }

    public async Task<PetParentProfileResponse> CompletePetParentProfileAsync(
        string firebaseUserId,
        CompletePetParentProfileRequest request,
        CancellationToken cancellationToken)
    {
        var normalisedFirebaseUserId = Required(firebaseUserId, nameof(firebaseUserId));
        var firstName = Required(request.FirstName, nameof(request.FirstName));
        var lastName = Required(request.LastName, nameof(request.LastName));
        var gender = NormalizeGender(request.Gender);
        var mobileCountryCode = Required(request.MobileCountryCode, nameof(request.MobileCountryCode));
        var mobileNumber = Required(request.MobileNumber, nameof(request.MobileNumber));
        var addressLine = Required(request.AddressLine, nameof(request.AddressLine));
        var zipCode = Required(request.ZipCode, nameof(request.ZipCode));
        var city = Required(request.City, nameof(request.City));
        var description = Required(request.Description, nameof(request.Description));
        var latitude = ValidateLatitude(request.Latitude);
        var longitude = ValidateLongitude(request.Longitude);

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(
            connection,
            "Parent.CompletePetParentProfile");

        // The sproc looks up the auth identity by FirebaseUserId internally —
        // the body never carries a ParentAuthIdentityId, so a malicious caller
        // can't complete another user's profile.
        command.Parameters.AddWithValue("@FirebaseUserId", normalisedFirebaseUserId);
        command.Parameters.AddWithValue("@FirstName", firstName);
        command.Parameters.AddWithValue("@LastName", lastName);
        command.Parameters.AddWithValue("@Gender", gender);
        command.Parameters.AddWithValue("@MobileCountryCode", mobileCountryCode);
        command.Parameters.AddWithValue("@MobileNumber", mobileNumber);
        command.Parameters.AddWithValue("@DateOfBirth", request.DateOfBirth.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@AddressLine", addressLine);
        command.Parameters.AddWithValue("@Latitude", latitude);
        command.Parameters.AddWithValue("@Longitude", longitude);
        command.Parameters.AddWithValue("@ZipCode", zipCode);
        command.Parameters.AddWithValue("@City", city);
        command.Parameters.AddWithValue("@Description", description);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Pet parent profile was not returned after save.");
            }

            return ReadPetParentProfile(reader);
        }
        catch (SqlException exception) when (exception.Number == 51200)
        {
            throw new ParentAuthIdentityNotFoundException(normalisedFirebaseUserId);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            throw new PetParentMobileNumberAlreadyExistsException(mobileCountryCode, mobileNumber);
        }
    }

    public async Task<UpdatePetParentProfilePhotoResponse> UpdateProfilePhotoAsync(
        Guid petParentId,
        string profilePhotoUrl,
        CancellationToken cancellationToken)
    {
        var url = Required(profilePhotoUrl, nameof(profilePhotoUrl));

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(
            connection,
            "Parent.UpdatePetParentProfilePhoto");

        command.Parameters.AddWithValue("@PetParentId", petParentId);
        command.Parameters.AddWithValue("@ProfilePhotoUrl", url);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Pet parent profile photo result was not returned.");
            }

            return new UpdatePetParentProfilePhotoResponse(
                reader.GetGuid(0),
                reader.GetString(1),
                new DateTimeOffset(reader.GetDateTime(2), TimeSpan.Zero));
        }
        catch (SqlException exception) when (exception.Number == 51201)
        {
            throw new PetParentNotFoundException(petParentId);
        }
    }

    private static PetParentProfileResponse ReadPetParentProfile(SqlDataReader reader)
    {
        return new PetParentProfileResponse(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            DateOnly.FromDateTime(reader.GetDateTime(7)),
            reader.GetString(8),
            reader.GetDecimal(9),
            reader.GetDecimal(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : new DateTimeOffset(reader.GetDateTime(15), TimeSpan.Zero),
            new DateTimeOffset(reader.GetDateTime(16), TimeSpan.Zero),
            new DateTimeOffset(reader.GetDateTime(17), TimeSpan.Zero));
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
            var unsupported => throw new UnsupportedPetParentGenderException(unsupported)
        };
    }

    private static decimal ValidateLatitude(decimal value)
    {
        if (value < -90m || value > 90m)
        {
            throw new ArgumentException($"Latitude '{value}' is outside the valid range -90..90.", nameof(value));
        }

        return value;
    }

    private static decimal ValidateLongitude(decimal value)
    {
        if (value < -180m || value > 180m)
        {
            throw new ArgumentException($"Longitude '{value}' is outside the valid range -180..180.", nameof(value));
        }

        return value;
    }

    public async Task<SendPetParentMobileOtpResponse> SendMobileOtpAsync(
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        var otpCode = GenerateOtpCode();

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(
            connection,
            "Parent.CreateMobileVerificationOtp");

        command.Parameters.AddWithValue("@PetParentId", petParentId);
        command.Parameters.AddWithValue("@OtpCode", otpCode);

        SendPetParentMobileOtpResponse response;

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Pet parent mobile OTP entry was not returned after save.");
            }

            response = new SendPetParentMobileOtpResponse(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero),
                new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero));
        }
        catch (SqlException exception) when (exception.Number == 51210)
        {
            throw new PetParentNotFoundException(petParentId);
        }

        await otpSender.SendMobileOtpAsync(
            response.PetParentId,
            response.MobileCountryCode,
            response.MobileNumber,
            otpCode,
            cancellationToken);

        return response;
    }

    public async Task<VerifyPetParentMobileOtpResponse> VerifyMobileOtpAsync(
        Guid petParentId,
        Guid parentMobileOtpId,
        VerifyPetParentMobileOtpRequest request,
        CancellationToken cancellationToken)
    {
        var otpCode = Required(request.OtpCode, nameof(request.OtpCode));

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(
            connection,
            "Parent.VerifyMobileVerificationOtp");

        command.Parameters.AddWithValue("@PetParentId", petParentId);
        command.Parameters.AddWithValue("@ParentMobileOtpId", parentMobileOtpId);
        command.Parameters.AddWithValue("@OtpCode", otpCode);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Pet parent mobile OTP validation result was not returned.");
            }

            return new VerifyPetParentMobileOtpResponse(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetBoolean(2),
                reader.GetString(3),
                new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero),
                reader.IsDBNull(5) ? null : new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero),
                new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero));
        }
        catch (SqlException exception) when (exception.Number == 51211)
        {
            throw new ParentMobileOtpNotFoundException(parentMobileOtpId);
        }
    }

    private static string GenerateOtpCode()
    {
        return RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    public async Task<UpsertPetParentIdentityResponse> UpsertIdentityAsync(
        Guid petParentId,
        string identityType,
        string identityPhotoUrl,
        CancellationToken cancellationToken)
    {
        var normalisedIdentityType = NormaliseIdentityType(identityType);
        var photoUrl = Required(identityPhotoUrl, nameof(identityPhotoUrl));

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(
            connection,
            "Parent.UpsertPetParentIdentity");

        command.Parameters.AddWithValue("@PetParentId", petParentId);
        command.Parameters.AddWithValue("@IdentityType", normalisedIdentityType);
        command.Parameters.AddWithValue("@IdentityPhotoUrl", photoUrl);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Pet parent identity row was not returned after upsert.");
            }

            return new UpsertPetParentIdentityResponse(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero),
                new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero));
        }
        catch (SqlException exception) when (exception.Number == 51206)
        {
            throw new PetParentNotFoundException(petParentId);
        }
    }

    public async Task<ResolvePetParentByFirebaseUidResponse> ResolvePetParentByFirebaseUidAsync(
        string firebaseUserId,
        CancellationToken cancellationToken)
    {
        var normalised = Required(firebaseUserId, nameof(firebaseUserId));

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(
            connection,
            "Parent.GetPetParentByFirebaseUid");
        command.Parameters.AddWithValue("@FirebaseUserId", normalised);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new ParentAuthIdentityNotFoundException(normalised);
        }

        return new ResolvePetParentByFirebaseUidResponse(
            reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetBoolean(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.GetBoolean(7),
            reader.IsDBNull(8) ? null : new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero));
    }

    private static string NormaliseIdentityType(string? value)
    {
        return Required(value, nameof(value)) switch
        {
            "Passport" => "Passport",
            "DriverLicense" => "DriverLicense",
            "NationalId" => "NationalId",
            "ResidencePermit" => "ResidencePermit",
            var unsupported => throw new UnsupportedPetParentIdentityTypeException(unsupported)
        };
    }

    private static SqlCommand CreateStoredProcedureCommand(SqlConnection connection, string storedProcedureName)
    {
        return new SqlCommand(storedProcedureName, connection)
        {
            CommandType = CommandType.StoredProcedure
        };
    }

    private async Task<string> GetSqlConnectionStringAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return configuredConnectionString;
        }

        if (secretProvider is null)
        {
            throw new InvalidOperationException(
                "SQL Server connection string is not configured and no Key Vault secret provider is registered.");
        }

        return await secretProvider.GetSqlConnectionStringAsync(cancellationToken);
    }

    private static ParentFirebaseAuthResponse ReadAuthIdentity(SqlDataReader reader)
    {
        return new ParentFirebaseAuthResponse(
            reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetBoolean(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetString(11),
            new DateTimeOffset(reader.GetDateTime(12), TimeSpan.Zero),
            new DateTimeOffset(reader.GetDateTime(13), TimeSpan.Zero),
            new DateTimeOffset(reader.GetDateTime(14), TimeSpan.Zero));
    }

    private static string NormalizeAuthProvider(string? value)
    {
        return Required(value, nameof(value)) switch
        {
            "Google" => "Google",
            "Apple" => "Apple",
            "EmailPassword" => "EmailPassword",
            var unsupported => throw new UnsupportedParentAuthProviderException(unsupported)
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

    private static object DbValue(string? value)
    {
        return value is null ? DBNull.Value : value;
    }
}
