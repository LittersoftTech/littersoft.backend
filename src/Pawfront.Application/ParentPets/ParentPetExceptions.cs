namespace Pawfront.Application.ParentPets;

public sealed class UnsupportedPetTypeException(string petType)
    : Exception($"Pet type '{petType}' is not supported.");

public sealed class UnsupportedPetGenderException(string gender)
    : Exception($"Pet gender '{gender}' is not supported.");

public sealed class MicrochipIdAlreadyExistsException(string microchipId)
    : Exception($"Microchip id '{microchipId}' is already registered to another pet.");

public sealed class PetNotFoundException(Guid petId)
    : Exception($"Pet '{petId}' was not found.");

public sealed class PetPhotoNotFoundException(Guid petPhotoId)
    : Exception($"Pet photo '{petPhotoId}' was not found.");

public sealed class UnsupportedVaccinationStatusException(string vaccinationStatus)
    : Exception($"Vaccination status '{vaccinationStatus}' is not supported.");

public sealed class UnsupportedSterilizationStatusException(string sterilizationStatus)
    : Exception($"Sterilization status '{sterilizationStatus}' is not supported.");

public sealed class UnsupportedTemperamentException(string temperament)
    : Exception($"Temperament '{temperament}' is not supported.");
