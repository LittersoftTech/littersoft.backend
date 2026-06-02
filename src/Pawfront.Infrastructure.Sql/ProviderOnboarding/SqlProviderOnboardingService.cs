using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.ProviderOnboarding;
using Pawfront.Contracts.ProviderOnboarding;

namespace Pawfront.Infrastructure.Sql.ProviderOnboarding;

internal sealed class SqlProviderOnboardingService(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider,
    IProviderMobileOtpSender otpSender) : IProviderOnboardingService
{
    public async Task<ProviderFirebaseAuthResponse> SaveFirebaseAuthAsync(
        SaveProviderFirebaseAuthCommand commandInput,
        CancellationToken cancellationToken)
    {
        var authProvider = NormalizeAuthProvider(commandInput.AuthProvider);
        var firebaseUserId = Required(commandInput.FirebaseUserId, nameof(commandInput.FirebaseUserId));
        var email = Required(commandInput.Email, nameof(commandInput.Email));

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(
            connection,
            "Provider.SaveProviderAuthIdentity");

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
            throw new InvalidOperationException("Provider auth identity was not returned after save.");
        }

        return ReadAuthIdentity(reader);
    }

    public async Task<ProviderProfileResponse> CompleteProviderProfileAsync(
        CompleteProviderProfileRequest request,
        CancellationToken cancellationToken)
    {
        var firstName = Required(request.FirstName, nameof(request.FirstName));
        var lastName = Required(request.LastName, nameof(request.LastName));
        var gender = NormalizeGender(request.Gender);
        var mobileCountryCode = Required(request.MobileCountryCode, nameof(request.MobileCountryCode));
        var mobileNumber = Required(request.MobileNumber, nameof(request.MobileNumber));

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(
            connection,
            "Provider.CompleteProviderProfile");

        command.Parameters.AddWithValue("@ProviderAuthIdentityId", request.ProviderAuthIdentityId);
        command.Parameters.AddWithValue("@FirstName", firstName);
        command.Parameters.AddWithValue("@LastName", lastName);
        command.Parameters.AddWithValue("@Gender", gender);
        command.Parameters.AddWithValue("@MobileCountryCode", mobileCountryCode);
        command.Parameters.AddWithValue("@MobileNumber", mobileNumber);
        command.Parameters.AddWithValue("@DateOfBirth", request.DateOfBirth.ToDateTime(TimeOnly.MinValue));

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Provider profile was not returned after save.");
            }

            return ReadProviderProfile(reader);
        }
        catch (SqlException exception) when (exception.Number == 51001)
        {
            throw new ProviderAuthIdentityNotFoundException(request.ProviderAuthIdentityId);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            throw new MobileNumberAlreadyExistsException(mobileCountryCode, mobileNumber);
        }
    }

    public async Task<ProviderProfileResponse> GetProviderProfileAsync(
        Guid providerId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(
            connection,
            "Provider.GetProviderProfile");
        command.Parameters.AddWithValue("@ProviderId", providerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new ProviderProfileNotFoundException(providerId);
        }

        return ReadProviderProfile(reader);
    }

    public async Task<ResolveProviderByFirebaseUidResponse> ResolveProviderByFirebaseUidAsync(
        string firebaseUserId,
        CancellationToken cancellationToken)
    {
        var normalized = Required(firebaseUserId, nameof(firebaseUserId));

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(
            connection,
            "Provider.GetProviderByFirebaseUid");
        command.Parameters.AddWithValue("@FirebaseUserId", normalized);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new ProviderAuthIdentityForFirebaseUserNotFoundException(normalized);
        }

        return new ResolveProviderByFirebaseUidResponse(
            reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetBoolean(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.GetBoolean(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : new DateTimeOffset(reader.GetDateTime(9), TimeSpan.Zero),
            reader.IsDBNull(10) ? null : reader.GetBoolean(10));
    }

    public async Task<SetActiveStatusOutcome> SetActiveStatusAsync(
        Guid providerId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(
            connection,
            "Provider.SetProviderActiveStatus");
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@IsActive", isActive);

        try
        {
            // The sproc emits exactly one result set:
            //   - on success: 3 cols (ProviderId, IsActive, UpdatedAtUtc)
            //   - on conflict: 10 cols (BookingId, ServiceId, ServiceCategory,
            //                           SubCategory, PetParentId, Source, CustomerName,
            //                           BookingDate, StartTime, EndTime)
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (reader.FieldCount == 10)
            {
                var conflicts = new List<ActiveStatusConflictingBooking>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    conflicts.Add(new ActiveStatusConflictingBooking(
                        BookingId: reader.GetGuid(0),
                        ServiceId: reader.GetGuid(1),
                        ServiceCategory: reader.GetString(2),
                        SubCategory: reader.GetString(3),
                        PetParentId: reader.IsDBNull(4) ? null : reader.GetGuid(4),
                        Source: reader.GetString(5),
                        CustomerName: reader.IsDBNull(6) ? null : reader.GetString(6),
                        BookingDate: DateOnly.FromDateTime(reader.GetDateTime(7)),
                        StartTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(8)),
                        EndTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(9))));
                }
                return new SetActiveStatusOutcome.BookingsExist(conflicts);
            }

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "SetProviderActiveStatus did not return the expected result row.");
            }

            return new SetActiveStatusOutcome.Updated(
                reader.GetGuid(0),
                reader.GetBoolean(1),
                new DateTimeOffset(reader.GetDateTime(2), TimeSpan.Zero));
        }
        catch (SqlException exception) when (exception.Number == 51100)
        {
            throw new ProviderProfileNotFoundException(providerId);
        }
    }

    public async Task<SendProviderMobileOtpResponse> SendProviderMobileOtpAsync(
        Guid providerId,
        CancellationToken cancellationToken)
    {
        var otpCode = GenerateOtpCode();

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(
            connection,
            "Provider.CreateMobileVerificationOtp");

        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@OtpCode", otpCode);

        SendProviderMobileOtpResponse response;

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Provider mobile OTP entry was not returned after save.");
            }

            response = ReadMobileOtp(reader);
        }
        catch (SqlException exception) when (exception.Number == 51002)
        {
            throw new ProviderProfileNotFoundException(providerId);
        }

        await otpSender.SendMobileOtpAsync(
            response.ProviderId,
            response.MobileCountryCode,
            response.MobileNumber,
            otpCode,
            cancellationToken);

        return response;
    }

    public async Task<VerifyProviderMobileOtpResponse> VerifyProviderMobileOtpAsync(
        Guid providerId,
        Guid providerMobileOtpId,
        VerifyProviderMobileOtpRequest request,
        CancellationToken cancellationToken)
    {
        var otpCode = Required(request.OtpCode, nameof(request.OtpCode));

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(
            connection,
            "Provider.VerifyMobileVerificationOtp");

        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@ProviderMobileOtpId", providerMobileOtpId);
        command.Parameters.AddWithValue("@OtpCode", otpCode);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Provider mobile OTP validation result was not returned.");
            }

            return ReadMobileOtpVerification(reader);
        }
        catch (SqlException exception) when (exception.Number == 51003)
        {
            throw new ProviderMobileOtpNotFoundException(providerMobileOtpId);
        }
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

    private static ProviderFirebaseAuthResponse ReadAuthIdentity(SqlDataReader reader)
    {
        return new ProviderFirebaseAuthResponse(
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

    private static ProviderProfileResponse ReadProviderProfile(SqlDataReader reader)
    {
        return new ProviderProfileResponse(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            DateOnly.FromDateTime(reader.GetDateTime(7)),
            reader.IsDBNull(8) ? null : new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero),
            reader.GetString(9),
            reader.GetBoolean(10),
            new DateTimeOffset(reader.GetDateTime(11), TimeSpan.Zero),
            new DateTimeOffset(reader.GetDateTime(12), TimeSpan.Zero));
    }

    private static SendProviderMobileOtpResponse ReadMobileOtp(SqlDataReader reader)
    {
        return new SendProviderMobileOtpResponse(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero),
            new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero));
    }

    private static VerifyProviderMobileOtpResponse ReadMobileOtpVerification(SqlDataReader reader)
    {
        return new VerifyProviderMobileOtpResponse(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetBoolean(2),
            reader.GetString(3),
            new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero),
            reader.IsDBNull(5) ? null : new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero),
            new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero));
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
