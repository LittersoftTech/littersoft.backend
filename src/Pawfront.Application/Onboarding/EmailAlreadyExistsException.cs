namespace Pawfront.Application.Onboarding;

public sealed class EmailAlreadyExistsException()
    : Exception("Email is already registered to another account of the same type.");
