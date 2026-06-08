using Pawfront.Application.ParentOnboarding;

namespace Pawfront.Infrastructure.Sql.ParentOnboarding;

/// <summary>
/// Placeholder sender used until a real SMS gateway is wired. The OTP is
/// stored hashed in SQL and returned to the client only via its "last two
/// digits" hint — dev/test verification happens by reading the most recent
/// row in [Parent].[ParentMobileOtps] for the parent.
/// </summary>
internal sealed class NoOpPetParentMobileOtpSender : IPetParentMobileOtpSender
{
    public Task SendMobileOtpAsync(
        Guid petParentId,
        string mobileCountryCode,
        string mobileNumber,
        string otpCode,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
