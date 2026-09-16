namespace Pawfront.Application.Providers;

/// <summary>
/// Wire-level values for the parent-facing "Provider Type" filter — is this a
/// registered business (a hotel / shop / school / clinic / shelter) or one
/// person working for themselves?
/// </summary>
/// <remarks>
/// The distinction is already carried by the offering's SUB-CATEGORY, so this is
/// a classification rather than new data. The freelance sub-category names live
/// in the five Cosmos registries (each has its own <c>SubCategories.Freelance</c>
/// constant); they are repeated here as an explicit set rather than matched on a
/// <c>StartsWith("Freelance")</c> prefix, so renaming one is a compile-time
/// decision instead of a filter that silently starts returning everybody.
/// </remarks>
public static class ProviderTypeFilters
{
    public const string RegisteredBusiness = "RegisteredBusiness";
    public const string Freelancer = "Freelancer";

    /// <summary>
    /// Every sub-category that means "one person, not a business". Mirrors the
    /// <c>SubCategories.Freelance</c> constants in the five Cosmos registries.
    /// PetAdoptionAndSale's is the bare <c>"Freelance"</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> FreelanceSubCategories =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "FreelancePetSitter",
            "FreelanceGroomer",
            "FreelanceTrainer",
            "FreelanceVeterinarian",
            "Freelance"
        };

    /// <summary>
    /// Trims + validates an incoming provider-type value. Blank means "no
    /// filter" and returns null. Throws <see cref="ArgumentException"/> on an
    /// unrecognised value so the endpoint can answer 400 rather than silently
    /// returning every provider.
    /// </summary>
    public static string? NormaliseOrNull(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim() switch
        {
            RegisteredBusiness => RegisteredBusiness,
            Freelancer => Freelancer,
            var unsupported => throw new ArgumentException(
                $"Provider type '{unsupported}' is not supported. " +
                $"Use {RegisteredBusiness} or {Freelancer}.")
        };
    }

    /// <summary>
    /// Does a search hit's sub-category satisfy the requested provider type?
    /// A null filter matches everything.
    /// </summary>
    public static bool Matches(string? filter, string subCategory) =>
        filter switch
        {
            null => true,
            Freelancer => FreelanceSubCategories.Contains(subCategory),
            _ => !FreelanceSubCategories.Contains(subCategory)
        };
}

/// <summary>
/// The payment methods a parent can insist a provider accepts. Deliberately the
/// SAME two values <c>Provider.ProviderPayoutMethods.PayoutMethod</c> stores and
/// <c>POST /providers/{id}/policy/payout-methods</c> writes — the filter is
/// asking about that exact set, so a second vocabulary would only invite drift.
/// </summary>
/// <remarks>
/// The design's third option, "Both", is the DEFAULT rather than a value: it
/// means no constraint, which on the wire is simply omitting the parameter.
/// Supplying several values means the provider must accept EVERY one of them —
/// a parent who can only pay cash is asking "do you take cash", and a provider
/// who takes only digital is not an answer to that.
/// </remarks>
public static class ProviderPaymentMethodFilters
{
    /// <summary>
    /// Trims, de-duplicates and validates the requested methods against
    /// <see cref="Policies.ProviderPayoutMethods.Allowed"/> — the same set the
    /// policy endpoint writes, deliberately NOT a second copy of it. Null/empty
    /// means "no filter". Throws <see cref="ArgumentException"/> on an
    /// unrecognised value.
    /// </summary>
    public static IReadOnlyCollection<string>? NormaliseOrNull(IReadOnlyCollection<string>? raw)
    {
        if (raw is null || raw.Count == 0)
        {
            return null;
        }

        var normalised = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in raw)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            var match = Policies.ProviderPayoutMethods.Allowed.FirstOrDefault(
                m => string.Equals(m, trimmed, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                throw new ArgumentException(
                    $"Payment method '{trimmed}' is not supported. Use " +
                    $"{Policies.ProviderPayoutMethods.Cash} or " +
                    $"{Policies.ProviderPayoutMethods.Digital}.");
            }

            normalised.Add(match);
        }

        return normalised.Count == 0 ? null : normalised;
    }
}

/// <summary>
/// Sort keys for the five per-service provider searches.
/// </summary>
/// <remarks>
/// "Distance" is deliberately absent. It needs a reference coordinate the server
/// does not have — neither the parent's device position nor a decision about
/// whether their saved address stands in for it — so offering the key before
/// that lands would mean silently sorting by something other than what the
/// parent asked for.
/// </remarks>
public enum ProviderSearchSortBy
{
    /// <summary>The service's headline charge. Hits with no price sort last.</summary>
    Price = 0,

    /// <summary>
    /// Completed bookings across all the provider's services — the design's
    /// "Sort by Booking (Most First / Least First)", i.e. how busy they are.
    /// </summary>
    Bookings = 1,

    /// <summary>
    /// How many pets the provider can take at once (the offering's capacity).
    /// </summary>
    PetCapacity = 2
}

/// <summary>
/// Parses the provider-search sort key. Blank means "no sort" — the searches
/// return candidates in discovery order, which is what they did before sorting
/// existed and what every current caller relies on.
/// </summary>
public static class ProviderSearchQueryParsing
{
    public static ProviderSearchSortBy? ParseSortBy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Enum.TryParse<ProviderSearchSortBy>(value.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException(
                $"Unsupported sortBy '{value}'. Expected 'Price', 'Bookings' or 'PetCapacity'.",
                nameof(value));
    }
}
