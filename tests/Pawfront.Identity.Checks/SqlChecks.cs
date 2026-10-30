using Microsoft.Data.SqlClient;
using Pawfront.Application.Badges;
using Pawfront.Application.ParentOnboarding;
using Pawfront.Application.ParentPets;

static class SqlChecks
{
    public static async Task<int> RunAsync()
    {
        const string cs = @"Server=(localdb)\MSSQLLocalDB;Database=PawfrontIdentityChecks;Integrated Security=true;Encrypt=false";
        var assembly = typeof(Pawfront.Infrastructure.Sql.SqlServiceRegistration).Assembly;
        T Make<T>(string type, params object?[] args) => (T)Activator.CreateInstance(assembly.GetType(type)!, args)!;
        var onboarding = Make<IParentOnboardingService>("Pawfront.Infrastructure.Sql.ParentOnboarding.SqlParentOnboardingService", cs, null, null);
        var review = Make<IParentIdentityReviewService>("Pawfront.Infrastructure.Sql.ParentOnboarding.SqlParentIdentityReviewService", cs, null);
        var pets = Make<IParentPetService>("Pawfront.Infrastructure.Sql.ParentPets.SqlParentPetService", cs, null);
        var badges = Make<IBadgeReader>("Pawfront.Infrastructure.Sql.Badges.SqlBadgeReader", cs, null);
        var parent = Guid.NewGuid(); var auth = Guid.NewGuid(); var checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
        await using var connection = new SqlConnection(cs); await connection.OpenAsync();
        async Task Execute(string text)
        {
            await using var command = new SqlCommand(text, connection);
            command.Parameters.AddWithValue("@Parent", parent); command.Parameters.AddWithValue("@Auth", auth);
            await command.ExecuteNonQueryAsync();
        }
        try
        {
            await Execute("""
                INSERT Parent.ParentAuthIdentities(ParentAuthIdentityId,FirebaseUserId,AuthProvider,Email)
                VALUES(@Auth,CONVERT(NVARCHAR(36),@Auth),N'EmailPassword',CONCAT(@Auth,N'@identity.test.invalid'));
                INSERT Parent.PetParents(PetParentId,ParentAuthIdentityId,FirstName,LastName,Gender,MobileCountryCode,MobileNumber,DateOfBirth,AddressLine,Latitude,Longitude,ZipCode,City,Description)
                VALUES(@Parent,@Auth,N'Identity',N'Test',N'Other',N'+41',LEFT(CONVERT(NVARCHAR(36),@Parent),16),'1990-01-01',N'Test',47,8,N'8000',N'Zurich',N'Test');
                """);
            var upload = await onboarding.UpsertIdentityAsync(parent, "Other", "https://test.invalid/private", default);
            Check(upload.Status == "InReview" && upload.RejectionReason is null, "SQL upload reader includes review fields");
            Check(!(await badges.ReadAsync([parent], [], [], default)).Parents.Contains(parent), "SQL pending identity has no badge");
            var owner = (await onboarding.GetIdentityAsync(parent, default))!;
            await review.ReviewAsync(owner.ParentIdentityId, new("Reject", owner.UpdatedAtUtc, "Blurry photo"), default);
            owner = (await onboarding.GetIdentityAsync(parent, default))!;
            Check(owner.Status == "NotApproved" && owner.RejectionReason == "Blurry photo", "SQL owner reader preserves rejection");
            upload = await onboarding.UpsertIdentityAsync(parent, "Passport", "https://test.invalid/replacement", default);
            await review.ReviewAsync(upload.ParentIdentityId, new("Accept", upload.UpdatedAtUtc), default);
            Check((await badges.ReadAsync([parent], [], [], default)).Parents.Contains(parent), "SQL accept enables parent badge");
            Check((await review.ListAsync("Added", default)).Any(i => i.Identity.PetParentId == parent && i.FirstName == "Identity" && i.LastName == "Test"), "Admin sees account name beside document");
            try { await review.ReviewAsync(upload.ParentIdentityId, new("Accept", upload.UpdatedAtUtc), default); throw new Exception("Expected conflict"); }
            catch (IdentityReviewConflictException) { checks++; }
            // C# reader passes all seven fractional seconds to the concurrency predicate.
            upload = await onboarding.UpsertIdentityAsync(parent, "Other", "https://test.invalid/new", default);
            Check(!(await badges.ReadAsync([parent], [], [], default)).Parents.Contains(parent), "SQL replacement removes badge immediately");
            await onboarding.DeleteIdentityAsync(parent, default);
            Check(await onboarding.GetIdentityAsync(parent, default) is null, "SQL delete during review succeeds");
            var chip = "756" + Random.Shared.NextInt64(100000000000, 999999999999);
            var pet = await pets.AddPetAsync(parent, new("Dog", "Milo", "Labrador", "Male", new(2020, 1, 1), 20, chip, null), default);
            Check((await badges.ReadAsync([], [pet.PetId], [], default)).Pets.Contains(pet.PetId), "SQL saved chip immediately enables pet badge");
            try { await pets.AddPetAsync(parent, new("Dog", "Other", "Labrador", "Male", new(2020, 1, 1), 20, chip, null), default); throw new Exception("Expected duplicate chip"); }
            catch (MicrochipIdAlreadyExistsException) { checks++; }
            await pets.UpdatePetAsync(pet.PetId, new("Dog", "Milo", "Labrador", "Male", new(2020, 1, 1), 20, chip, null), default);
            Check((await badges.ReadAsync([], [pet.PetId], [], default)).Pets.Contains(pet.PetId), "Saving same chip on same pet is allowed");
            await pets.UpdatePetAsync(pet.PetId, new("Dog", "Milo", "Labrador", "Male", new(2020, 1, 1), 20, null, null), default);
            Check(!(await badges.ReadAsync([], [pet.PetId], [], default)).Pets.Contains(pet.PetId), "Removing chip clears pet badge");
        }
        finally
        {
            await Execute("DELETE Notification.NotificationOutbox WHERE RecipientId=@Parent; DELETE Parent.Pets WHERE PetParentId=@Parent; DELETE Parent.PetParents WHERE PetParentId=@Parent; DELETE Parent.ParentAuthIdentities WHERE ParentAuthIdentityId=@Auth;");
        }
        return checks;
    }
}
