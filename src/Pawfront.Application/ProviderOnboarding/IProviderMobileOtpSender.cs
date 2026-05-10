namespace Pawfront.Application.ProviderOnboarding;

public interface IProviderMobileOtpSender
{
    Task SendMobileOtpAsync(
        Guid providerId,
        string mobileCountryCode,
        string mobileNumber,
        string otpCode,
        CancellationToken cancellationToken);
}
