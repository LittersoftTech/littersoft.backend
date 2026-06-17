using System.Text;

namespace Pawfront.Domain.Vocabularies;

/// <summary>A single vocabulary value: the stable <see cref="Code"/> stored and
/// sent over the wire, plus a human-friendly <see cref="DisplayName"/> for UI.</summary>
public readonly record struct VocabularyItem(string Code, string DisplayName);

/// <summary>
/// Projects the vocabulary enums into the two shapes the rest of the system
/// needs: a fast lookup set of valid codes (for registry/service validation)
/// and a code+displayName list (for the mobile metadata endpoint). The enums
/// are the single source of truth — everything here is derived from them, so
/// adding an enum value automatically flows into validation and metadata.
/// </summary>
public static class VocabularyCatalog
{
    public static IReadOnlyList<VocabularyItem> Animals { get; } = Build<Animal>();
    public static IReadOnlyList<VocabularyItem> Behaviours { get; } = Build<Behaviour>();

    public static IReadOnlySet<string> AnimalCodes { get; } = ToCodeSet(Animals);
    public static IReadOnlySet<string> BehaviourCodes { get; } = ToCodeSet(Behaviours);

    private static IReadOnlyList<VocabularyItem> Build<TEnum>() where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>()
            .Select(value => value.ToString())
            .Select(code => new VocabularyItem(code, Humanize(code)))
            .ToArray();

    private static IReadOnlySet<string> ToCodeSet(IReadOnlyList<VocabularyItem> items) =>
        items.Select(item => item.Code).ToHashSet(StringComparer.Ordinal);

    // "GuineaPig" -> "Guinea Pig": insert a space at each lower->upper boundary.
    private static string Humanize(string code)
    {
        var builder = new StringBuilder(code.Length + 4);
        for (var i = 0; i < code.Length; i++)
        {
            if (i > 0 && char.IsUpper(code[i]) && !char.IsUpper(code[i - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(code[i]);
        }

        return builder.ToString();
    }
}
