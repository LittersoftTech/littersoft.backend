using Microsoft.Data.SqlClient;
using Pawfront.Application.DeviceTokens;
using Pawfront.Application.Notifications;
using Pawfront.Application.ProviderOnboarding;

static class SqlChecks
{
    public static async Task<int> RunAsync()
    {
        const string cs = @"Server=(localdb)\MSSQLLocalDB;Database=PawfrontEmailBookingChecks;Integrated Security=true;Encrypt=false";
        var assembly = typeof(Pawfront.Infrastructure.Sql.SqlServiceRegistration).Assembly;
        T Make<T>(string name, params object?[] args) => (T)Activator.CreateInstance(assembly.GetType(name)!, args)!;
        var onboarding = Make<IProviderOnboardingService>("Pawfront.Infrastructure.Sql.ProviderOnboarding.SqlProviderOnboardingService", cs, null, null);
        var devices = Make<IProviderDeviceTokenService>("Pawfront.Infrastructure.Sql.DeviceTokens.SqlProviderDeviceTokenService", cs, null);
        var reader = Make<IProviderPushTokenReader>("Pawfront.Infrastructure.Sql.DeviceTokens.SqlProviderPushTokenReader", cs, null);
        var suffix = Guid.NewGuid().ToString("N");
        var aUid = "security-a-" + suffix; var bUid = "security-b-" + suffix;
        var phone = "phone-" + suffix; var secondPhone = "second-" + suffix;
        var aToken = "token-a-" + suffix; var bToken = "token-b-" + suffix; var aSecondToken = "a-second-" + suffix;
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        Task<Pawfront.Contracts.ProviderOnboarding.ProviderFirebaseAuthResponse> SignIn(string uid, string? token = null, string? device = null)
            => onboarding.SaveFirebaseAuthAsync(new(uid, "EmailPassword", "password", uid + "@example.invalid", true,
                null, null, null, null, token, device, "Android"), default);
        var authA = await SignIn(aUid, aToken, phone);
        var authB = await SignIn(bUid);
        await using var connection = new SqlConnection(cs); await connection.OpenAsync();
        async Task Execute(string sql, Guid? provider = null)
        {
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@A", a); command.Parameters.AddWithValue("@B", b);
            command.Parameters.AddWithValue("@AuthA", authA.ProviderAuthIdentityId); command.Parameters.AddWithValue("@AuthB", authB.ProviderAuthIdentityId);
            command.Parameters.AddWithValue("@Provider", provider ?? a);
            command.Parameters.AddWithValue("@Phone", phone);
            await command.ExecuteNonQueryAsync();
        }
        async Task<int> Count(string where)
        {
            await using var command = new SqlCommand("SELECT COUNT(*) FROM Provider.ProviderDeviceTokens WHERE " + where, connection);
            command.Parameters.AddWithValue("@Phone", phone); command.Parameters.AddWithValue("@A", a);
            command.Parameters.AddWithValue("@B", b); command.Parameters.AddWithValue("@AuthA", authA.ProviderAuthIdentityId);
            return (int)(await command.ExecuteScalarAsync())!;
        }
        await Execute("""
            INSERT Provider.Providers(ProviderId,ProviderAuthIdentityId,FirstName,LastName,Gender,MobileCountryCode,MobileNumber,DateOfBirth)
            VALUES(@A,@AuthA,N'Security',N'A',N'Other',N'+41',LEFT(CONVERT(NVARCHAR(36),@A),16),'1990-01-01'),
                  (@B,@AuthB,N'Security',N'B',N'Other',N'+41',LEFT(CONVERT(NVARCHAR(36),@B),16),'1990-01-01');
            UPDATE Provider.ProviderAuthIdentities SET ProviderId=@A WHERE ProviderAuthIdentityId=@AuthA;
            UPDATE Provider.ProviderAuthIdentities SET ProviderId=@B WHERE ProviderAuthIdentityId=@AuthB;
            UPDATE Provider.ProviderDeviceTokens SET ProviderId=@A WHERE ProviderAuthIdentityId=@AuthA;
            """);
        await devices.RegisterAsync(aUid, new(aSecondToken, secondPhone, "Android"), default);
        await SignIn(bUid, bToken, "  " + phone + "  ");
        Check((await reader.GetCurrentTokensAsync(aUid, default)).SequenceEqual([aSecondToken]), "Sign-in removes previous owner's same-phone token but preserves their other phone");
        Check((await reader.GetCurrentTokensAsync(bUid, default)).SequenceEqual([bToken]), "New account owns the current phone token");
        Check(await Count("DeviceId=@Phone AND ProviderId=@A") == 0, "Previous account has no remaining association with this device");
        await devices.RegisterAsync(aUid, new(bToken, phone, "Android"), default);
        Check((await reader.GetCurrentTokensAsync(bUid, default)).Count == 0, "Explicit refresh transfers an existing token to the new owner");
        try { await devices.DeactivateAsync(bUid, bToken, default); throw new Exception("Old account deactivated transferred token"); }
        catch (DeviceTokenNotFoundException) { checks++; }
        var rotated = "rotated-" + suffix;
        await devices.RegisterAsync(bUid, new(rotated, phone, "Android"), default);
        Check((await reader.GetCurrentTokensAsync(aUid, default)).SequenceEqual([aSecondToken]), "Token rotation on account switch removes old account association");
        await devices.DeactivateAsync(bUid, rotated, default);
        Check((await reader.GetCurrentTokensAsync(bUid, default)).Count == 0, "Logout suppresses provider pushes");
        await SignIn(bUid, rotated, phone);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => devices.RegisterAsync(
            i % 2 == 0 ? aUid : bUid, new("race-" + i + "-" + suffix, phone, "Android"), default)));
        Check(await Count("DeviceId=@Phone AND IsActive=1") == 1, "Concurrent registrations leave one active device owner");
        Check(await Count("DeviceId=@Phone AND ProviderId=@A") == 0 || await Count("DeviceId=@Phone AND ProviderId=@B") == 0,
            "Concurrent account switches leave associations with only one provider");
        await SignIn(aUid, aToken, phone);
        await Execute("EXEC Provider.DeleteProvider @ProviderId=@Provider;", a);
        Check(await Count("ProviderId=@A OR ProviderAuthIdentityId=@AuthA") == 0, "Account deletion removes all provider tokens");
        Check((await reader.GetCurrentTokensAsync(aUid, default)).Count == 0, "Deleted account cannot receive a queued push");
        await Execute("""
            INSERT Provider.ProviderDeviceTokens(ProviderAuthIdentityId,ProviderId,FcmToken,DeviceId)
            VALUES(@AuthA,@A,CONCAT(N'stale-',@A),CONCAT(N'stale-device-',@A));
            EXEC Provider.DeleteProvider @ProviderId=@A;
            """);
        Check(await Count("ProviderId=@A OR ProviderAuthIdentityId=@AuthA") == 0, "Repeated deletion also clears stale provider tokens");
        return checks;
    }
}
