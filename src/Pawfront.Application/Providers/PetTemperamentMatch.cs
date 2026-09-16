namespace Pawfront.Application.Providers;

/// <summary>
/// Does a provider take the temperament of the pet the parent is shopping for?
/// </summary>
/// <remarks>
/// <para>
/// The five searches and the browse list already take a <c>petId</c>, but only
/// ever used the pet's TYPE as a filter — its temperament was never consulted, so
/// a parent with an aggressive dog was shown sitters who had said they take
/// friendly dogs only, with nothing on the card to say so. This answers that
/// question per card so the app can bucket a non-match into its "Other" section
/// rather than presenting it as available.
/// </para>
/// <para>
/// A HINT, NOT A FILTER, and deliberately so: a parent whose dog is aggressive
/// would otherwise see a near-empty list with no explanation, and the provider
/// they can still ring up would have vanished. The explicit
/// <c>?dogTemperaments=</c> query parameter remains the hard filter, for when the
/// parent has actually asked to narrow the list.
/// </para>
/// <para>
/// The two must AGREE about what counts as a match, which is why the "recorded
/// none" case answers <c>false</c> rather than <c>null</c>: the filter excludes a
/// provider with an empty list ("a parent asking for someone comfortable with an
/// anxious dog is not answered by silence"), so a flag that called that
/// unanswerable would put the same provider in two different buckets on two
/// surfaces.
/// </para>
/// </remarks>
public static class PetTemperamentMatch
{
    /// <summary>
    /// <c>true</c> / <c>false</c> when the question can be answered, <c>null</c>
    /// when it cannot: no pet was named, the pet has no temperament recorded (it
    /// is optional on a pet profile), or the provider's category records no
    /// temperament list at all — vet, trainer and adoption-and-sale offerings hold
    /// none, so reporting <c>false</c> for them would read as a refusal they never
    /// made.
    /// </summary>
    /// <param name="petTemperament">
    /// The temperament on the pet the search was filtered by (Anxious / Friendly /
    /// Aggressive), or null.
    /// </param>
    /// <param name="providerTemperaments">
    /// What the provider recorded. Null = the category has no such field; EMPTY =
    /// the provider recorded none, which is a <c>false</c>.
    /// </param>
    public static bool? Evaluate(
        string? petTemperament,
        IReadOnlyCollection<string>? providerTemperaments)
    {
        if (string.IsNullOrWhiteSpace(petTemperament) || providerTemperaments is null)
        {
            return null;
        }

        var wanted = petTemperament.Trim();
        foreach (var recorded in providerTemperaments)
        {
            if (recorded is not null
                && string.Equals(recorded.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
