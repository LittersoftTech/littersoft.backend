namespace Pawfront.Domain.Vocabularies;

/// <summary>
/// Canonical, single-source-of-truth list of animals understood across the
/// whole platform. Persisted as the enum NAME (e.g. "Dog") in Cosmos offering
/// documents and SQL columns, and surfaced to the mobile app via the metadata
/// endpoint. Add a value here to extend the vocabulary everywhere at once —
/// there is no per-category copy of these strings any more.
/// </summary>
public enum Animal
{
    Dog,
    Cat,
    Hamster,
    GuineaPig,
    Bird,
    Rabbit,
    Horse,
    Reptile
}
