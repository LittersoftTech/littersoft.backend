namespace Pawfront.Domain.Vocabularies;

/// <summary>
/// Canonical pet behaviour / temperament vocabulary, shared by pet profiles
/// (<c>Parent.Pets.Temperament</c>) and the per-provider "temperaments handled"
/// lists for PetSitter and PetGroomer (<c>DogTemperaments</c>).
///
/// PetTrainer deliberately keeps its own, richer training-temperament list
/// (Calm/Energetic/Sensitive/Hyperactive/…) — that is a different axis (training
/// suitability) and is NOT mapped onto this enum.
/// </summary>
public enum Behaviour
{
    Anxious,
    Friendly,
    Aggressive,
    HyperActive,
    Shy,
    Calm,
    Playful,
    Independent,
    Protective
}
