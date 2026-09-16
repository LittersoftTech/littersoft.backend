using Pawfront.Application.Earnings;
using Pawfront.Application.ParentOnboarding;
using Pawfront.Application.Providers;
using Pawfront.Domain.Vocabularies;
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
///
/// The parent app's filter sheet adds four dimensions on top of the per-service
/// ones, all shared by every search here and all handled by
/// <see cref="BuildRefinements"/>: provider type (registered business vs
/// freelancer), accepted payment methods, dog temperament, and a sort key.
/// "Location" on that sheet is the pre-existing <c>city</c> parameter — the four
/// cities it offers are the app's own picker, deliberately not a server-side
/// enum, so a new city is a mobile release rather than a backend one.
///
/// DOG TEMPERAMENT is exposed on the day-care, night-stay and groomer searches
/// only: those are the two categories whose offerings record a
/// <c>dogTemperaments</c> list. Vets and trainers hold none, so offering the
/// parameter there would be offering a filter that could only ever return
/// nothing.
///
/// DISTANCE sorting is deliberately absent from <c>sortBy</c> — see
/// <see cref="ProviderSearchSortBy"/>.
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
        // The parent app's shared filter sheet. Repeated query values bind to a
        // string[]: ?dogTemperaments=Anxious&dogTemperaments=Friendly.
        string? providerType,
        string[]? paymentMethods,
        string[]? dogTemperaments,
        string? sortBy,
        string? sortDirection,
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

        var (error, animals, petTemperament) = await ResolveAnimalsFromPetAsync(
            petId, currentPetParent, ownershipReader, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var (refinementError, refinements) = BuildRefinements(
            providerType, paymentMethods, dogTemperaments, sortBy, sortDirection);
        if (refinementError is not null)
        {
            return refinementError;
        }

        var (clampedSkip, clampedTake) = ClampPaging(skip, take);
        var results = await searchService.SearchDayCareAsync(
            new DayCareProviderSearchCriteria(
                animals, city, normalisedLocation, date, startTime, endTime,
                clampedSkip, clampedTake, refinements, petTemperament),
            cancellationToken);

        return ApiResults.Ok(results.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> SearchNightStay(
        Guid? petId,
        DateOnly? startDate,
        DateOnly? pickupDate,
        string? city,
        string? serviceLocation,
        // The parent app's shared filter sheet. Repeated query values bind to a
        // string[]: ?dogTemperaments=Anxious&dogTemperaments=Friendly.
        string? providerType,
        string[]? paymentMethods,
        string[]? dogTemperaments,
        string? sortBy,
        string? sortDirection,
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

        var (error, animals, petTemperament) = await ResolveAnimalsFromPetAsync(
            petId, currentPetParent, ownershipReader, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var (refinementError, refinements) = BuildRefinements(
            providerType, paymentMethods, dogTemperaments, sortBy, sortDirection);
        if (refinementError is not null)
        {
            return refinementError;
        }

        var (clampedSkip, clampedTake) = ClampPaging(skip, take);
        var results = await searchService.SearchNightStayAsync(
            new NightStayProviderSearchCriteria(
                animals, city, normalisedLocation, startDate, pickupDate,
                clampedSkip, clampedTake, refinements, petTemperament),
            cancellationToken);

        return ApiResults.Ok(results.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> SearchGroomers(
        Guid? petId,
        DateOnly? date,
        string? serviceItemCode,
        string? city,
        string? serviceLocation,
        // The parent app's shared filter sheet. Repeated query values bind to a
        // string[]: ?dogTemperaments=Anxious&dogTemperaments=Friendly.
        string? providerType,
        string[]? paymentMethods,
        string[]? dogTemperaments,
        string? sortBy,
        string? sortDirection,
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

        var (error, animals, petTemperament) = await ResolveAnimalsFromPetAsync(
            petId, currentPetParent, ownershipReader, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var (refinementError, refinements) = BuildRefinements(
            providerType, paymentMethods, dogTemperaments, sortBy, sortDirection);
        if (refinementError is not null)
        {
            return refinementError;
        }

        var (clampedSkip, clampedTake) = ClampPaging(skip, take);
        var results = await searchService.SearchGroomingAsync(
            new GroomingProviderSearchCriteria(
                animals, city, normalisedLocation, date, normalisedCode,
                clampedSkip, clampedTake, refinements, petTemperament),
            cancellationToken);

        return ApiResults.Ok(results.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> SearchVets(
        Guid? petId,
        DateOnly? date,
        string? city,
        string? serviceLocation,
        // The parent app's shared filter sheet, minus dogTemperaments — this
        // category's offering records no temperament list.
        string? providerType,
        string[]? paymentMethods,
        string? sortBy,
        string? sortDirection,
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

        var (error, animals, petTemperament) = await ResolveAnimalsFromPetAsync(
            petId, currentPetParent, ownershipReader, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var (refinementError, refinements) = BuildRefinements(
            providerType, paymentMethods, dogTemperaments: null, sortBy, sortDirection);
        if (refinementError is not null)
        {
            return refinementError;
        }

        var (clampedSkip, clampedTake) = ClampPaging(skip, take);
        var results = await searchService.SearchVetAsync(
            new VetProviderSearchCriteria(
                animals, city, normalisedLocation, date, clampedSkip, clampedTake, refinements,
                petTemperament),
            cancellationToken);

        return ApiResults.Ok(results.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> SearchTrainers(
        Guid? petId,
        DateOnly? date,
        string? city,
        string? serviceLocation,
        // The parent app's shared filter sheet, minus dogTemperaments — this
        // category's offering records no temperament list.
        string? providerType,
        string[]? paymentMethods,
        string? sortBy,
        string? sortDirection,
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

        var (error, animals, petTemperament) = await ResolveAnimalsFromPetAsync(
            petId, currentPetParent, ownershipReader, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var (refinementError, refinements) = BuildRefinements(
            providerType, paymentMethods, dogTemperaments: null, sortBy, sortDirection);
        if (refinementError is not null)
        {
            return refinementError;
        }

        var (clampedSkip, clampedTake) = ClampPaging(skip, take);
        var results = await searchService.SearchTrainerAsync(
            new TrainerProviderSearchCriteria(
                animals, city, normalisedLocation, date, clampedSkip, clampedTake, refinements,
                petTemperament),
            cancellationToken);

        return ApiResults.Ok(results.Select(ToResponse).ToArray());
    }

    /// <summary>
    /// Validates the four cross-search filter/sort parameters in one place, so the
    /// five endpoints cannot drift on what they accept or on the error code they
    /// answer with. Returns an error result on the first unusable value, mirroring
    /// the (Error, Value) shape <see cref="ResolveAnimalsFromPetAsync"/> already
    /// uses in this file.
    /// </summary>
    private static (IResult? Error, ProviderSearchRefinements Refinements) BuildRefinements(
        string? providerType,
        string[]? paymentMethods,
        string[]? dogTemperaments,
        string? sortBy,
        string? sortDirection)
    {
        var empty = new ProviderSearchRefinements();

        string? normalisedProviderType;
        try
        {
            normalisedProviderType = ProviderTypeFilters.NormaliseOrNull(providerType);
        }
        catch (ArgumentException exception)
        {
            return (ApiResults.BadRequest("UnsupportedProviderType", exception.Message), empty);
        }

        IReadOnlyCollection<string>? normalisedPayments;
        try
        {
            normalisedPayments = ProviderPaymentMethodFilters.NormaliseOrNull(paymentMethods);
        }
        catch (ArgumentException exception)
        {
            return (ApiResults.BadRequest("UnsupportedPaymentMethod", exception.Message), empty);
        }

        IReadOnlyCollection<string>? normalisedTemperaments;
        try
        {
            normalisedTemperaments = NormaliseTemperamentsOrNull(dogTemperaments);
        }
        catch (ArgumentException exception)
        {
            return (ApiResults.BadRequest("UnsupportedTemperament", exception.Message), empty);
        }

        ProviderSearchSortBy? parsedSortBy;
        EarningsSortDirection parsedDirection;
        try
        {
            parsedSortBy = ProviderSearchQueryParsing.ParseSortBy(sortBy);
            // Shared with the earnings and history lists on purpose: a client that
            // learns sortDirection=Asc on one API should not find another spells it
            // differently.
            parsedDirection = EarningsQueryParsing.ParseSortDirection(sortDirection);
        }
        catch (ArgumentException exception)
        {
            return (ApiResults.BadRequest("InvalidRequest", exception.Message), empty);
        }

        return (null, new ProviderSearchRefinements(
            normalisedProviderType, normalisedPayments, normalisedTemperaments,
            parsedSortBy, parsedDirection));
    }

    /// <summary>
    /// Validates the requested temperaments against the canonical Behaviour
    /// vocabulary, so a typo is a 400 rather than a silently empty result set —
    /// the same reasoning behind validating the grooming service-item code.
    /// </summary>
    private static IReadOnlyCollection<string>? NormaliseTemperamentsOrNull(string[]? raw)
    {
        if (raw is null || raw.Length == 0)
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
            if (!VocabularyCatalog.BehaviourCodes.Contains(trimmed))
            {
                throw new ArgumentException(
                    $"Temperament '{trimmed}' is not supported. Expected one of: " +
                    string.Join(", ", VocabularyCatalog.BehaviourCodes) + ".");
            }
            normalised.Add(trimmed);
        }

        return normalised.Count == 0 ? null : normalised;
    }

    /// <summary>
    /// petId → the pet's type as the animal filter, and its temperament as a HINT
    /// stamped on every card (never a filter — see PetTemperamentMatch). Ownership
    /// is enforced inline (these routes aren't under /pets/{petId}, so the group
    /// filter doesn't apply) with the same status codes as OwnedPetFilter.
    /// </summary>
    private static async Task<(IResult? Error, string[]? Animals, string? Temperament)> ResolveAnimalsFromPetAsync(
        Guid? petId,
        ICurrentPetParentContext currentPetParent,
        IPetParentOwnershipReader ownershipReader,
        CancellationToken cancellationToken)
    {
        if (petId is null)
        {
            return (null, null, null);
        }

        var callerPetParentId = await currentPetParent.GetPetParentIdAsync(cancellationToken);
        if (callerPetParentId is null)
        {
            return (ApiResults.Forbidden(
                "ParentProfileNotCompleted",
                "Complete the parent profile (POST /api/v1/parent-onboarding/profile) before accessing this resource."), null, null);
        }

        var pet = await ownershipReader.GetPetLookupAsync(petId.Value, cancellationToken);
        if (pet is null)
        {
            return (ApiResults.NotFound("PetNotFound", $"Pet '{petId.Value}' was not found."), null, null);
        }
        if (pet.OwningPetParentId != callerPetParentId.Value)
        {
            return (ApiResults.Forbidden(
                "Forbidden",
                "You can only filter by pets belonging to your own profile."), null, null);
        }

        return (null, [pet.PetType], pet.Temperament);
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
            result.Description,
            result.ImageUrl,
            result.BannerImageUrl,
            result.PetCapacity,
            result.MatchesPetTemperament);
}
