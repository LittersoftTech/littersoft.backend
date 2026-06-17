using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.ParentOnboarding;
using Pawfront.Application.ParentPets;
using Pawfront.Contracts.ParentPets;
using Pawfront.Domain.Vocabularies;

namespace Pawfront.Infrastructure.Sql.ParentPets;

internal sealed class SqlParentPetService(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IParentPetService
{
    public async Task<PetParentPetResponse> AddPetAsync(
        Guid petParentId,
        AddPetParentPetRequest request,
        CancellationToken cancellationToken)
    {
        var petType = NormalizePetType(request.PetType);
        var petName = Required(request.PetName, nameof(request.PetName));
        var breed = Required(request.Breed, nameof(request.Breed));
        var gender = NormalizeGender(request.Gender);
        var weight = ValidateWeight(request.Weight);
        var microchipId = TrimOrNull(request.MicrochipId);
        var description = TrimOrNull(request.Description);

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(
            connection,
            "Parent.AddPetParentPet");

        command.Parameters.AddWithValue("@PetParentId", petParentId);
        command.Parameters.AddWithValue("@PetType", petType);
        command.Parameters.AddWithValue("@PetName", petName);
        command.Parameters.AddWithValue("@Breed", breed);
        command.Parameters.AddWithValue("@Gender", gender);
        command.Parameters.AddWithValue("@DateOfBirth", request.DateOfBirth.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@Weight", weight);
        command.Parameters.AddWithValue("@MicrochipId", DbValue(microchipId));
        command.Parameters.AddWithValue("@Description", DbValue(description));

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Pet row was not returned after insert.");
            }

            return ReadPet(reader);
        }
        catch (SqlException exception) when (exception.Number == 51202)
        {
            throw new PetParentNotFoundException(petParentId);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627 && microchipId is not null)
        {
            throw new MicrochipIdAlreadyExistsException(microchipId);
        }
    }

    public async Task<PetParentPetResponse> UpdatePetAsync(
        Guid petId,
        UpdatePetParentPetRequest request,
        CancellationToken cancellationToken)
    {
        var petType = NormalizePetType(request.PetType);
        var petName = Required(request.PetName, nameof(request.PetName));
        var breed = Required(request.Breed, nameof(request.Breed));
        var gender = NormalizeGender(request.Gender);
        var weight = ValidateWeight(request.Weight);
        var microchipId = TrimOrNull(request.MicrochipId);
        var description = TrimOrNull(request.Description);

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(
            connection,
            "Parent.UpdatePetParentPet");

        command.Parameters.AddWithValue("@PetId", petId);
        command.Parameters.AddWithValue("@PetType", petType);
        command.Parameters.AddWithValue("@PetName", petName);
        command.Parameters.AddWithValue("@Breed", breed);
        command.Parameters.AddWithValue("@Gender", gender);
        command.Parameters.AddWithValue("@DateOfBirth", request.DateOfBirth.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@Weight", weight);
        command.Parameters.AddWithValue("@MicrochipId", DbValue(microchipId));
        command.Parameters.AddWithValue("@Description", DbValue(description));

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Pet row was not returned after update.");
            }

            return ReadPet(reader);
        }
        catch (SqlException exception) when (exception.Number == 51205)
        {
            throw new PetNotFoundException(petId);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627 && microchipId is not null)
        {
            throw new MicrochipIdAlreadyExistsException(microchipId);
        }
    }

    public async Task<PetParentPetResponse> UpdateMedicalInfoAsync(
        Guid petId,
        UpdatePetMedicalInfoRequest request,
        CancellationToken cancellationToken)
    {
        var vaccinationStatus = NormalizeVaccinationStatus(request.VaccinationStatus);
        var sterilizationStatus = NormalizeSterilizationStatus(request.SterilizationStatus);
        var temperament = NormalizeTemperament(request.Temperament);
        var medicalHistory = TrimOrNull(request.MedicalHistory);

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(
            connection,
            "Parent.UpdatePetMedicalInfo");

        command.Parameters.AddWithValue("@PetId", petId);
        command.Parameters.AddWithValue("@VaccinationStatus", vaccinationStatus);
        command.Parameters.AddWithValue("@SterilizationStatus", sterilizationStatus);
        command.Parameters.AddWithValue("@MedicalHistory", DbValue(medicalHistory));
        command.Parameters.AddWithValue("@Temperament", temperament);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Pet row was not returned after medical-info update.");
            }

            return ReadPet(reader);
        }
        catch (SqlException exception) when (exception.Number == 51203)
        {
            throw new PetNotFoundException(petId);
        }
    }

    public async Task<IReadOnlyList<PetParentPetWithPhotosResponse>> GetPetsAsync(
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(
            connection,
            "Parent.ListPetParentPets");
        command.Parameters.AddWithValue("@PetParentId", petParentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // Result set 1: pets. We read into an intermediate list and an index
        // by PetId so we can hang photos off the right row when set 2 lands.
        var pets = new List<(PetParentPetWithPhotosResponseBuilder Builder, List<PetPhotoResponse> Photos)>();
        var index = new Dictionary<Guid, List<PetPhotoResponse>>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var builder = new PetParentPetWithPhotosResponseBuilder(
                PetId: reader.GetGuid(0),
                PetParentId: reader.GetGuid(1),
                PetType: reader.GetString(2),
                PetName: reader.GetString(3),
                Breed: reader.GetString(4),
                Gender: reader.GetString(5),
                DateOfBirth: DateOnly.FromDateTime(reader.GetDateTime(6)),
                Weight: reader.GetDecimal(7),
                MicrochipId: reader.IsDBNull(8) ? null : reader.GetString(8),
                Description: reader.IsDBNull(9) ? null : reader.GetString(9),
                VaccinationStatus: reader.IsDBNull(10) ? null : reader.GetString(10),
                SterilizationStatus: reader.IsDBNull(11) ? null : reader.GetString(11),
                MedicalHistory: reader.IsDBNull(12) ? null : reader.GetString(12),
                Temperament: reader.IsDBNull(13) ? null : reader.GetString(13),
                CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(14), TimeSpan.Zero),
                UpdatedAtUtc: new DateTimeOffset(reader.GetDateTime(15), TimeSpan.Zero));

            var photos = new List<PetPhotoResponse>();
            pets.Add((builder, photos));
            index[builder.PetId] = photos;
        }

        // Result set 2: photos. Bucket each row by PetId; rows for pets we
        // didn't see in set 1 are ignored (shouldn't happen given the sproc's
        // INNER JOIN, but be defensive).
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var petId = reader.GetGuid(1);
                if (!index.TryGetValue(petId, out var bucket))
                {
                    continue;
                }

                bucket.Add(new PetPhotoResponse(
                    PetPhotoId: reader.GetGuid(0),
                    PetId: petId,
                    PhotoUrl: reader.GetString(2),
                    CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero),
                    UpdatedAtUtc: new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero)));
            }
        }

        return pets
            .Select(entry => entry.Builder.Build(entry.Photos))
            .ToList();
    }

    public async Task<PetParentPetWithPhotosResponse?> GetPetAsync(
        Guid petId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(
            connection,
            "Parent.GetPetParentPet");
        command.Parameters.AddWithValue("@PetId", petId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // Result set 1: the pet (zero or one row).
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var builder = new PetParentPetWithPhotosResponseBuilder(
            PetId: reader.GetGuid(0),
            PetParentId: reader.GetGuid(1),
            PetType: reader.GetString(2),
            PetName: reader.GetString(3),
            Breed: reader.GetString(4),
            Gender: reader.GetString(5),
            DateOfBirth: DateOnly.FromDateTime(reader.GetDateTime(6)),
            Weight: reader.GetDecimal(7),
            MicrochipId: reader.IsDBNull(8) ? null : reader.GetString(8),
            Description: reader.IsDBNull(9) ? null : reader.GetString(9),
            VaccinationStatus: reader.IsDBNull(10) ? null : reader.GetString(10),
            SterilizationStatus: reader.IsDBNull(11) ? null : reader.GetString(11),
            MedicalHistory: reader.IsDBNull(12) ? null : reader.GetString(12),
            Temperament: reader.IsDBNull(13) ? null : reader.GetString(13),
            CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(14), TimeSpan.Zero),
            UpdatedAtUtc: new DateTimeOffset(reader.GetDateTime(15), TimeSpan.Zero));

        // Result set 2: the pet's photo gallery (oldest-first).
        var photos = new List<PetPhotoResponse>();
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                photos.Add(new PetPhotoResponse(
                    PetPhotoId: reader.GetGuid(0),
                    PetId: reader.GetGuid(1),
                    PhotoUrl: reader.GetString(2),
                    CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero),
                    UpdatedAtUtc: new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero)));
            }
        }

        return builder.Build(photos);
    }

    /// <summary>
    /// Mutable shim for assembling a <see cref="PetParentPetWithPhotosResponse"/>
    /// across two result sets — the photo list isn't known until set 2 lands.
    /// </summary>
    private sealed record PetParentPetWithPhotosResponseBuilder(
        Guid PetId,
        Guid PetParentId,
        string PetType,
        string PetName,
        string Breed,
        string Gender,
        DateOnly DateOfBirth,
        decimal Weight,
        string? MicrochipId,
        string? Description,
        string? VaccinationStatus,
        string? SterilizationStatus,
        string? MedicalHistory,
        string? Temperament,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc)
    {
        public PetParentPetWithPhotosResponse Build(IReadOnlyList<PetPhotoResponse> photos) =>
            new(
                PetId,
                PetParentId,
                PetType,
                PetName,
                Breed,
                Gender,
                DateOfBirth,
                Weight,
                MicrochipId,
                Description,
                VaccinationStatus,
                SterilizationStatus,
                MedicalHistory,
                Temperament,
                photos,
                CreatedAtUtc,
                UpdatedAtUtc);
    }

    public async Task<PetPhotoResponse> AddPhotoAsync(
        Guid petId,
        string photoUrl,
        CancellationToken cancellationToken)
    {
        var url = Required(photoUrl, nameof(photoUrl));

        await using var connection = new SqlConnection(await GetSqlConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = CreateStoredProcedureCommand(
            connection,
            "Parent.AddPetPhoto");

        command.Parameters.AddWithValue("@PetId", petId);
        command.Parameters.AddWithValue("@PhotoUrl", url);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Pet photo row was not returned after insert.");
            }

            return new PetPhotoResponse(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero),
                new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero));
        }
        catch (SqlException exception) when (exception.Number == 51204)
        {
            throw new PetNotFoundException(petId);
        }
    }

    private static PetParentPetResponse ReadPet(SqlDataReader reader)
    {
        return new PetParentPetResponse(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            DateOnly.FromDateTime(reader.GetDateTime(6)),
            reader.GetDecimal(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            new DateTimeOffset(reader.GetDateTime(14), TimeSpan.Zero),
            new DateTimeOffset(reader.GetDateTime(15), TimeSpan.Zero));
    }

    private static string NormalizeVaccinationStatus(string? value)
    {
        return Required(value, nameof(value)) switch
        {
            "Vaccinated" => "Vaccinated",
            "NotVaccinated" => "NotVaccinated",
            var unsupported => throw new UnsupportedVaccinationStatusException(unsupported)
        };
    }

    private static string NormalizeSterilizationStatus(string? value)
    {
        return Required(value, nameof(value)) switch
        {
            "Sterilized" => "Sterilized",
            "Intact" => "Intact",
            var unsupported => throw new UnsupportedSterilizationStatusException(unsupported)
        };
    }

    // Behaviour is the canonical pet-temperament vocabulary (shared with the
    // provider "temperaments handled" lists).
    private static string NormalizeTemperament(string? value)
    {
        var raw = Required(value, nameof(value));
        return Enum.TryParse<Behaviour>(raw, ignoreCase: false, out var behaviour)
            ? behaviour.ToString()
            : throw new UnsupportedTemperamentException(raw);
    }

    // Pets draw from the canonical Animal vocabulary but are restricted to the
    // subset a parent can actually own. The spelling comes from the enum (one
    // source of truth); the subset is the only per-context rule.
    private static readonly IReadOnlySet<Animal> SupportedPetTypes = new HashSet<Animal>
    {
        Animal.Dog, Animal.Cat, Animal.Hamster, Animal.GuineaPig
    };

    private static string NormalizePetType(string? value)
    {
        var raw = Required(value, nameof(value));
        return Enum.TryParse<Animal>(raw, ignoreCase: false, out var animal) && SupportedPetTypes.Contains(animal)
            ? animal.ToString()
            : throw new UnsupportedPetTypeException(raw);
    }

    private static string NormalizeGender(string? value)
    {
        return Required(value, nameof(value)) switch
        {
            "Male" => "Male",
            "Female" => "Female",
            var unsupported => throw new UnsupportedPetGenderException(unsupported)
        };
    }

    private static decimal ValidateWeight(decimal value)
    {
        if (value <= 0m)
        {
            throw new ArgumentException($"Weight must be greater than zero (got {value}).", nameof(value));
        }

        if (value >= 1000m)
        {
            throw new ArgumentException($"Weight must be less than 1000 (got {value}).", nameof(value));
        }

        return value;
    }

    private static SqlCommand CreateStoredProcedureCommand(SqlConnection connection, string storedProcedureName)
    {
        return new SqlCommand(storedProcedureName, connection)
        {
            CommandType = CommandType.StoredProcedure
        };
    }

    private async Task<string> GetSqlConnectionStringAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return configuredConnectionString;
        }

        if (secretProvider is null)
        {
            throw new InvalidOperationException(
                "SQL Server connection string is not configured and no Key Vault secret provider is registered.");
        }

        return await secretProvider.GetSqlConnectionStringAsync(cancellationToken);
    }

    private static string Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", name);
        }

        return value.Trim();
    }

    private static string? TrimOrNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static object DbValue(string? value)
    {
        return value is null ? DBNull.Value : value;
    }
}
