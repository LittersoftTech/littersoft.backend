using Pawfront.Domain.Vocabularies;

namespace Pawfront.Contracts.Metadata;

/// <summary>
/// Reference vocabulary lists the mobile app uses to render its pickers
/// (pet types, animals handled, temperaments/behaviours) without hard-coding
/// the values client-side. Extend with new fields as more vocabularies move
/// behind the metadata endpoint.
/// </summary>
public sealed record MetadataResponse(
    IReadOnlyList<MetadataItem> Animals,
    IReadOnlyList<MetadataItem> Behaviours);

/// <summary>One selectable value: the stable <see cref="Code"/> the client
/// sends back, and a human-friendly <see cref="DisplayName"/> to show.</summary>
public sealed record MetadataItem(string Code, string DisplayName);

/// <summary>
/// Builds the <see cref="MetadataResponse"/> from the canonical domain
/// vocabularies. Shared by both API hosts so they always return identical data.
/// </summary>
public static class MetadataCatalog
{
    public static MetadataResponse Build() => new(
        Map(VocabularyCatalog.Animals),
        Map(VocabularyCatalog.Behaviours));

    private static IReadOnlyList<MetadataItem> Map(IReadOnlyList<VocabularyItem> items) =>
        items.Select(item => new MetadataItem(item.Code, item.DisplayName)).ToArray();
}
