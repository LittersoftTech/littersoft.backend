namespace Pawfront.Contracts.ParentOnboarding;

public sealed record EmailStatusRequest(string? Email);

public sealed record EmailStatusResponse(bool Registered);
