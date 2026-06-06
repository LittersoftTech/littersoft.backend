namespace Pawfront.Application.ParentOnboarding;

/// <summary>
/// Sends the raw OTP code to the parent's mobile via SMS. The raw code is
/// only available between OTP creation and this call — the persisted form
/// is the SHA-256 hash. A real SMS-gateway implementation will replace
/// the no-op once the provider/SMS contract is signed.
/// </summary>
public interface IPetParentMobileOtpSender
{
    Task SendMobileOtpAsync(
        Guid petParentId,
        string mobileCountryCode,
        string mobileNumber,
        string otpCode,
        CancellationToken cancellationToken);
}
