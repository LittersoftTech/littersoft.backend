namespace Pawfront.Infrastructure.Cosmos.Services.PetGroomer;

/// <summary>
/// Canonical list of grooming services a Pet Groomer provider can offer.
/// Server-defined: codes are stable identifiers (used in Cosmos docs +
/// booking rows + slot queries); display names are what the mobile picker
/// renders. Durations are per-groomer and live on each provider's offering
/// row, NOT here.
/// </summary>
internal static class GroomingServiceCatalog
{
    public static readonly IReadOnlyList<GroomingCatalogEntry> Entries = new GroomingCatalogEntry[]
    {
        new("WireCoatHandStripping",  "Wire Coat Hand Stripping"),
        new("PuppyFirstGroom",        "Puppy First Groom"),
        new("BreedSpecificStyling",   "Breed Specific Styling"),
        new("Dematting",              "De-matting"),
        new("BathDryAndBrush",        "Bath, Dry and Brush"),
        new("BathAndDry",             "Bath & Dry"),
        new("DeShedding",             "De-Shedding"),
        new("MedicatedBath",          "Medicated Bath"),
        new("TickAndFleaRemoval",     "Tick & Flea Removal"),
        new("CatGrooming",            "Cat Grooming"),
        new("CoatDyeing",             "Coat Dyeing"),
        new("NailsClipping",          "Nails Clipping"),
        new("EarCleaning",            "Ear Cleaning"),
        new("OralHygienePack",        "Oral Hygiene Pack"),
        new("AnalGlandExpression",    "Anal Gland Expression"),
        new("PawPadTrimming",         "Paw Pad Trimming"),
        new("SummerCoatPreparation",  "Summer Coat Preparation"),
        new("WinterCoatPreparation",  "Winter Coat Preparation"),
    };

    public static readonly IReadOnlySet<string> Codes =
        new HashSet<string>(Entries.Select(e => e.Code), StringComparer.Ordinal);

    public static bool TryGetDisplayName(string code, out string displayName)
    {
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Code, code, StringComparison.Ordinal))
            {
                displayName = entry.DisplayName;
                return true;
            }
        }
        displayName = string.Empty;
        return false;
    }
}

internal sealed record GroomingCatalogEntry(string Code, string DisplayName);
