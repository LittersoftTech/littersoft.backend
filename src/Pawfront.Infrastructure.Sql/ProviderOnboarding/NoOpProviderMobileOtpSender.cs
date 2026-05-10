using Pawfront.Application.ProviderOnboarding;

namespace Pawfront.Infrastructure.Sql.ProviderOnboarding;

internal sealed class NoOpProviderMobileOtpSender : IProviderMobileOtpSender
{
    public Task SendMobileOtpAsync(
        Guid providerId,
        string mobileCountryCode,
        string mobileNumber,
        string otpCode,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
