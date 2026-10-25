using Pawfront.Application.Onboarding;
using Pawfront.Application.ProviderOnboarding;

var assembly = typeof(Pawfront.Infrastructure.Sql.SqlServiceRegistration).Assembly;
var onboarding = (IProviderOnboardingService)Activator.CreateInstance(
    assembly.GetType("Pawfront.Infrastructure.Sql.ProviderOnboarding.InMemoryProviderOnboardingService")!, new object?[] { null })!;
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
    Console.WriteLine($"PASS {name}");
}
Task<Pawfront.Contracts.ProviderOnboarding.ProviderFirebaseAuthResponse> SignIn(string uid, string email) =>
    onboarding.SaveFirebaseAuthAsync(new(uid, "EmailPassword", null, email, true, "Test", "+41790000000", null, null, "device-token", null, null), default);

var auth = await SignIn("original-user", "returning@test.invalid");
var old = await onboarding.CompleteProviderProfileAsync(new(auth.ProviderAuthIdentityId,
    "Test", "Provider", "Other", "+41", "790000000", new(1990, 1, 1)), default);
await onboarding.SetActiveStatusAsync(old.ProviderId, false, false, default);
try { await SignIn("different-user", auth.Email); throw new Exception("Inactive account released its email"); }
catch (EmailAlreadyExistsException) { Check(true, "Inactive account still owns email"); }
var other = await SignIn("different-user", "other@test.invalid");
try
{
    await onboarding.CompleteProviderProfileAsync(new(other.ProviderAuthIdentityId,
        "Other", "Provider", "Other", "+41", "790000000", new(1990, 1, 1)), default);
    throw new Exception("Inactive account released its mobile");
}
catch (MobileNumberAlreadyExistsException) { Check(true, "Inactive account still owns mobile"); }

var deletion = await onboarding.DeleteProviderAccountAsync(old.ProviderId, default);
var deleted = await onboarding.GetProviderProfileAsync(old.ProviderId, default);
Check(!deleted.IsActive && deleted.FirstName == "Deleted" && deleted.MobileNumber != old.MobileNumber,
    "Deleted profile remains anonymised and disabled");
var freshAuth = await SignIn("new-firebase-user", auth.Email.ToUpperInvariant());
Check(freshAuth.ProviderAuthIdentityId != auth.ProviderAuthIdentityId && freshAuth.ProviderId is null,
    "Same email with new Firebase UID creates a fresh identity without old provider link");
var fresh = await onboarding.CompleteProviderProfileAsync(new(freshAuth.ProviderAuthIdentityId,
    "Returning", "Provider", "Other", "+41", "790000000", new(1990, 1, 1)), default);
Check(fresh.ProviderId != old.ProviderId && fresh.IsActive && fresh.MobileVerifiedAtUtc is null,
    "Same mobile creates a new provider requiring mobile verification");
Check((await SignIn("new-firebase-user", freshAuth.Email)).ProviderId == fresh.ProviderId,
    "Repeated sign-in resolves only the new provider");
var repeated = await onboarding.DeleteProviderAccountAsync(old.ProviderId, default);
Check(repeated.Summary.WasAlreadyDeleted && repeated.Summary.DeletedAtUtc == deletion.Summary.DeletedAtUtc,
    "Repeated deletion preserves deletion timestamp");
Check((await onboarding.ResolveProviderByFirebaseUidAsync("new-firebase-user", default)).ProviderId == fresh.ProviderId,
    "Deleting old provider again does not affect new account");
try { await SignIn("third-user", freshAuth.Email); throw new Exception("New live account did not own email"); }
catch (EmailAlreadyExistsException) { Check(true, "New live account blocks duplicate email"); }
try { await onboarding.SetActiveStatusAsync(old.ProviderId, true, false, default); throw new Exception("Old provider reactivated"); }
catch (ProviderAccountDeletedException) { Check(true, "Old provider cannot reactivate"); }
// A Firebase UID can also be reused without recovering the old identity.
var uidReuse = await SignIn("original-user", "uid-reuse@test.invalid");
Check(uidReuse.ProviderAuthIdentityId != auth.ProviderAuthIdentityId && uidReuse.ProviderId is null,
    "Reusing original Firebase UID cannot recover deleted provider");
Console.WriteLine($"{checks} provider re-registration checks passed.");
