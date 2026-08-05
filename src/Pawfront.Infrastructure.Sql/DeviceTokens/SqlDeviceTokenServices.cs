using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.DeviceTokens;
using Pawfront.Contracts.DeviceTokens;

namespace Pawfront.Infrastructure.Sql.DeviceTokens;

/// <summary>
/// Shared plumbing for the two host-specific device-token services. Both sproc
/// pairs take the same parameters and return the same nine columns, so only the
/// names differ.
/// </summary>
internal abstract class SqlDeviceTokenServiceBase(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider)
{
    protected abstract string SaveProcedureName { get; }
    protected abstract string DeactivateProcedureName { get; }

    /// <summary>THROW code the save sproc raises when the auth identity is missing.</summary>
    protected abstract int IdentityNotFoundErrorNumber { get; }

    /// <summary>THROW code the deactivate sproc raises when the token isn't the caller's.</summary>
    protected abstract int TokenNotFoundErrorNumber { get; }

    protected async Task<DeviceTokenResponse> RegisterCoreAsync(
        string firebaseUserId,
        RegisterDeviceTokenRequest request,
        CancellationToken cancellationToken)
    {
        var fcmToken = Required(request.FcmToken, nameof(request.FcmToken));
        // Throws ArgumentException for anything that isn't Android/iOS, so the
        // CHECK constraint is never the thing that reports a bad platform.
        var platform = DevicePlatforms.Normalize(request.DevicePlatform);
        var deviceId = string.IsNullOrWhiteSpace(request.DeviceId) ? null : request.DeviceId.Trim();

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(SaveProcedureName, connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        command.Parameters.AddWithValue("@FirebaseUserId", Required(firebaseUserId, nameof(firebaseUserId)));
        command.Parameters.AddWithValue("@FcmToken", fcmToken);
        command.Parameters.AddWithValue("@DeviceId", (object?)deviceId ?? DBNull.Value);
        command.Parameters.AddWithValue("@DevicePlatform", (object?)platform ?? DBNull.Value);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Device token row was not returned after save.");
            }

            return ReadDeviceToken(reader);
        }
        catch (SqlException exception) when (exception.Number == IdentityNotFoundErrorNumber)
        {
            throw new DeviceTokenIdentityNotFoundException(firebaseUserId);
        }
    }

    protected async Task<DeviceTokenResponse> DeactivateCoreAsync(
        string firebaseUserId,
        string fcmToken,
        CancellationToken cancellationToken)
    {
        var token = Required(fcmToken, nameof(fcmToken));

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(DeactivateProcedureName, connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        command.Parameters.AddWithValue("@FirebaseUserId", Required(firebaseUserId, nameof(firebaseUserId)));
        command.Parameters.AddWithValue("@FcmToken", token);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new DeviceTokenNotFoundException();
            }

            return ReadDeviceToken(reader);
        }
        catch (SqlException exception) when (exception.Number == TokenNotFoundErrorNumber)
        {
            throw new DeviceTokenNotFoundException();
        }
    }

    private static DeviceTokenResponse ReadDeviceToken(SqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetBoolean(4),
            reader.GetInt32(5),
            new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero),
            new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero),
            new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero));

    private static string Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", name);
        }

        return value.Trim();
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
}

internal sealed class SqlProviderDeviceTokenService(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider)
    : SqlDeviceTokenServiceBase(configuredConnectionString, secretProvider), IProviderDeviceTokenService
{
    protected override string SaveProcedureName => "Provider.SaveProviderDeviceToken";
    protected override string DeactivateProcedureName => "Provider.DeactivateProviderDeviceToken";
    protected override int IdentityNotFoundErrorNumber => 51004;
    protected override int TokenNotFoundErrorNumber => 51005;

    public Task<DeviceTokenResponse> RegisterAsync(
        string firebaseUserId, RegisterDeviceTokenRequest request, CancellationToken cancellationToken)
        => RegisterCoreAsync(firebaseUserId, request, cancellationToken);

    public Task<DeviceTokenResponse> DeactivateAsync(
        string firebaseUserId, string fcmToken, CancellationToken cancellationToken)
        => DeactivateCoreAsync(firebaseUserId, fcmToken, cancellationToken);
}

internal sealed class SqlPetParentDeviceTokenService(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider)
    : SqlDeviceTokenServiceBase(configuredConnectionString, secretProvider), IPetParentDeviceTokenService
{
    protected override string SaveProcedureName => "Parent.SaveParentDeviceToken";
    protected override string DeactivateProcedureName => "Parent.DeactivateParentDeviceToken";
    protected override int IdentityNotFoundErrorNumber => 51225;
    protected override int TokenNotFoundErrorNumber => 51226;

    public Task<DeviceTokenResponse> RegisterAsync(
        string firebaseUserId, RegisterDeviceTokenRequest request, CancellationToken cancellationToken)
        => RegisterCoreAsync(firebaseUserId, request, cancellationToken);

    public Task<DeviceTokenResponse> DeactivateAsync(
        string firebaseUserId, string fcmToken, CancellationToken cancellationToken)
        => DeactivateCoreAsync(firebaseUserId, fcmToken, cancellationToken);
}
