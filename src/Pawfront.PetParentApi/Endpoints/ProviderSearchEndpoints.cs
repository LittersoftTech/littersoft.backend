using Pawfront.Application.ParentOnboarding;
using Pawfront.Application.Providers;
using Pawfront.Application.Services.PetGroomer;
using Pawfront.Contracts.Providers;
using Pawfront.PetParentApi.Auth;

namespace Pawfront.PetParentApi.Endpoints;

/// <summary>
/// Per-service booking searches for the pet-parent app. One endpoint per
/// bookable experience (day care, night stay, grooming, vet) because the
/// filter vocabulary differs per service. All filters are optional and
/// combinable; date/time fields travel as complete groups. Results are
/// availability-checked against real slots when dates are supplied.
/// </summary>
internal static class ProviderSearchEndpoints
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;
    private const int MaxStayNights = 30;

    public static IEndpointRouteBuilder MapProviderSearchEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapGet("/providers/search/day-care", SearchDayCare);
        builder.MapGet("/providers/search/night-stay", SearchNightStay);
        builder.MapGet("/providers/search/groomers", SearchGroomers);
        builder.MapGet("/providers/search/vets", SearchVets);
        builder.MapGet("/providers/search/trainers", SearchTrainers);
        return builder;
    }

    private static async Task<IResult> SearchDayCare(
        Guid? petId,
        DateOnly? date,
        TimeOnly? startTime,
        TimeOnly? endTime,
        string? city,
        string? serviceLocation,
        int? skip,
        int? take,
        IProviderSearchService searchService,
        ICurrentPetParentContext currentPetParent,
        IPetParentOwnershipReader ownershipReader,
        CancellationToken cancellationToken)
    {
        string? normalisedLocation;
        try
        {
            normalisedLocation = NormaliseServiceLocationOrNull(serviceLocation);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("UnsupportedServiceLocation", exception.Message);
        }

        var windowParamCount = (date is null ? 0 : 1) + (startTime is null ? 0 : 1) + (endTime is null ? 0 : 1);
        if (windowParamCount is not 0 and not 3)
        {
            return ApiResults.BadRequest(
                "InvalidRequest",
                "date, startTime and endTime must be provided together.");
        }
        if (windowParamCount == 3 && startTime!.Value >= endTime!.Value)
        {
            return ApiResults.BadRequest("InvalidRequest", "startTime must be earlier than endTime.");
        }

        var (error, animals) = await ResolveAnimalsFromPetAsync(
            petId, currentPetParent, ownershipReader, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var (clampedSkip, clampedTake) = ClampPaging(skip, take);
        var results = await searchService.SearchDayCareAsync(
            new DayCareProviderSearchCriteria(
                animals, city, normalisedLocation, date, startTime, endTime, clampedSkip, clampedTake),
            cancellationToken);

        return ApiResults.Ok(results.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> SearchNightStay(
        Guid? petId,
        DateOnly? startDate,
        DateOnly? pickupDate,
        string? city,
        string? serviceLocation,
        int? skip,
        int? take,
        IProviderSearchService searchService,
        ICurrentPetParentContext currentPetParent,
        IPetParentOwnershipReader ownershipReader,
        CancellationToken cancellationToken)
    {
        string? normalisedLocation;
        try
        {
            normalisedLocation = NormaliseServiceLocationOrNull(serviceLocation);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("UnsupportedServiceLocation", exception.Message);
        }

        if (startDate is null != pickupDate is null)
        {
            return ApiResults.BadRequest(
                "InvalidRequest",
                "startDate and pickupDate must be provided together.");
        }
        if (startDate is not null)
        {
            if (startDate.Value >= pickupDate!.Value)
            {
                return ApiResults.BadRequest("InvalidRequest", "startDate must be earlier than pickupDate.");
            }
            if (pickupDate.Value.DayNumber - startDate.Value.DayNumber > MaxStayNights)
            {
                return ApiResults.BadRequest(
                    "InvalidRequest",
                    $"A night-stay search cannot span more than {MaxStayNights} nights.");
            }
        }

        var (error, animals) = await ResolveAnimalsFromPetAsync(
            petId, currentPetParent, ownershipReader, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var (clampedSkip, clampedTake) = ClampPaging(skip, take);
        var results = await searchService.SearchNightStayAsync(
            new NightStayProviderSearchCriteria(
                animals, city, normalisedLocation, startDate, pickupDate, clampedSkip, clampedTake),
            cancellationToken);

        return ApiResults.Ok(results.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> SearchGroomers(
        Guid? petId,
        DateOnly? date,
        string? serviceItemCode,
        string? city,
        string? serviceLocation,
        int? skip,
        int? take,
        IProviderSearchService searchService,
        IPetGroomerServiceRegistry petGroomerRegistry,
        ICurrentPetParentContext currentPetParent,
        IPetParentOwnershipReader ownershipReader,
        CancellationToken cancellationToken)
    {
        string? normalisedLocation;
        try
        {
            normalisedLocation = NormaliseServiceLocationOrNull(serviceLocation);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("UnsupportedServiceLocation", exception.Message);
        }

        // Validate the code against the canonical 18-entry grooming catalog so a
        // typo yields a 400 instead of a silently empty result set.
        var normalisedCode = string.IsNullOrWhiteSpace(serviceItemCode) ? null : serviceItemCode.Trim();
        if (normalisedCode is not null
            && !petGroomerRegistry.GetServiceCatalog().Any(
                e => string.Equals(e.Code, normalisedCode, StringComparison.Ordinal)))
        {
            return ApiResults.BadRequest(
                "UnsupportedServiceItemCode",
                $"Grooming service code '{normalisedCode}' is not in the service catalog.");
        }

        var (error, animals) = await ResolveAnimalsFromPetAsync(
            petId, currentPetParent, ownershipReader, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var (clampedSkip, clampedTake) = ClampPaging(skip, take);
        var results = await searchService.SearchGroomingAsync(
            new GroomingProviderSearchCriteria(
                animals, city, normalisedLocation, date, normalisedCode, clampedSkip, clampedTake),
            cancellationToken);

        return ApiResults.Ok(results.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> SearchVets(
        Guid? petId,
        DateOnly? date,
        string? city,
        string? serviceLocation,
        int? skip,
        int? take,
        IProviderSearchService searchService,
        ICurrentPetParentContext currentPetParent,
        IPetParentOwnershipReader ownershipReader,
        CancellationToken cancellationToken)
    {
        string? normalisedLocation;
        try
        {
            normalisedLocation = NormaliseServiceLocationOrNull(serviceLocation);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("UnsupportedServiceLocation", exception.Message);
        }

        var (error, animals) = await ResolveAnimalsFromPetAsync(
            petId, currentPetParent, ownershipReader, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var (clampedSkip, clampedTake) = ClampPaging(skip, take);
        var results = await searchService.SearchVetAsync(
            new VetProviderSearchCriteria(
                animals, city, normalisedLocation, date, clampedSkip, clampedTake),
            cancellationToken);

        return ApiResults.Ok(results.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> SearchTrainers(
        Guid? petId,
        DateOnly? date,
        string? city,
        string? serviceLocation,
        int? skip,
        int? take,
        IProviderSearchService searchService,
        ICurrentPetParentContext currentPetParent,
        IPetParentOwnershipReader ownershipReader,
        CancellationToken cancellationToken)
    {
        string? normalisedLocation;
        try
        {
            normalisedLocation = NormaliseServiceLocationOrNull(serviceLocation);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("UnsupportedServiceLocation", exception.Message);
        }

        var (error, animals) = await ResolveAnimalsFromPetAsync(
            petId, currentPetParent, ownershipReader, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var (clampedSkip, clampedTake) = ClampPaging(skip, take);
        var results = await searchService.SearchTrainerAsync(
            new TrainerProviderSearchCriteria(
                animals, city, normalisedLocation, date, clampedSkip, clampedTake),
            cancellationToken);

        return ApiResults.Ok(results.Select(ToResponse).ToArray());
    }

    /// <summary>
    /// petId → the pet's type as the animal filter. Ownership is enforced
    /// inline (these routes aren't under /pets/{petId}, so the group filter
    /// doesn't apply) with the same status codes as OwnedPetFilter.
    /// </summary>
    private static async Task<(IResult? Error, string[]? Animals)> ResolveAnimalsFromPetAsync(
        Guid? petId,
        ICurrentPetParentContext currentPetParent,
        IPetParentOwnershipReader ownershipReader,
        CancellationToken cancellationToken)
    {
        if (petId is null)
        {
            return (null, null);
        }

        var callerPetParentId = await currentPetParent.GetPetParentIdAsync(cancellationToken);
        if (callerPetParentId is null)
        {
            return (ApiResults.Forbidden(
                "ParentProfileNotCompleted",
                "Complete the parent profile (POST /api/v1/parent-onboarding/profile) before accessing this resource."), null);
        }

        var pet = await ownershipReader.GetPetLookupAsync(petId.Value, cancellationToken);
        if (pet is null)
        {
            return (ApiResults.NotFound("PetNotFound", $"Pet '{petId.Value}' was not found."), null);
        }
        if (pet.OwningPetParentId != callerPetParentId.Value)
        {
            return (ApiResults.Forbidden(
                "Forbidden",
                "You can only filter by pets belonging to your own profile."), null);
        }

        return (null, [pet.PetType]);
    }

    private static (int Skip, int Take) ClampPaging(int? skip, int? take) =>
        (Math.Max(0, skip ?? 0),
         take is null ? DefaultPageSize : Math.Clamp(take.Value, 1, MaxPageSize));

    private static string? NormaliseServiceLocationOrNull(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim() switch
        {
            ProviderServiceLocationFilters.ParentsPlace => ProviderServiceLocationFilters.ParentsPlace,
            ProviderServiceLocationFilters.ProvidersPlace => ProviderServiceLocationFilters.ProvidersPlace,
            var unsupported => throw new ArgumentException(
                $"Service location '{unsupported}' is not supported. Use ParentsPlace or ProvidersPlace.")
        };
    }

    private static ProviderSearchResultResponse ToResponse(ProviderSearchResult result) =>
        new(
            result.ProviderId,
            result.ServiceId,
            result.SubCategory,
            result.BusinessName,
            result.CompletedBookings,
            result.Charges,
            result.ChargesUnit,
            result.ServiceItemCode,
            result.ImageUrl);
}
