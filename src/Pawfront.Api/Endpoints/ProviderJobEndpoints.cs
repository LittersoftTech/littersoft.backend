using Pawfront.Api.Auth;
using Pawfront.Application.Earnings;
using Pawfront.Application.Jobs;
using Pawfront.Application.ProviderOnboarding;
using Pawfront.Contracts.Jobs;

namespace Pawfront.Api.Endpoints;

/// <summary>
/// The provider's JOB LIST — the agenda / "jobs to review" inbox behind their
/// filter sheet.
/// </summary>
/// <remarks>
/// <para>
/// A new route rather than filters bolted onto <c>GET /providers/{id}/bookings</c>:
/// that one is single-day only and returns a bare unpaged array, whereas the
/// filter sheet offers Day Care and Night Stay side by side and a multi-status
/// selection needs a total and a stable page. Changing it in place would have been
/// a breaking response-shape change for an endpoint the app already calls, so it
/// and the night-stay list are left exactly as they are.
/// </para>
/// <para>
/// It reads the same <c>Booking.BookingAmounts</c> definition the earnings screen
/// reads, so a job's amount here and the same job's amount there cannot drift —
/// which is what made a second, independent list acceptable.
/// </para>
/// <para>
/// Ownership is enforced from the JWT, unlike most of this host, which trusts the
/// route id. The list names the provider's customers, their pets and what each job
/// pays, so trusting the route would hand a competitor's customer list and revenue
/// to anyone who could guess a GUID — the same reasoning behind the earnings and
/// analytics routes.
/// </para>
/// </remarks>
internal static class ProviderJobEndpoints
{
    public static IEndpointRouteBuilder MapProviderJobEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapGet("/providers/{providerId:guid}/jobs", ListJobs);
        return builder;
    }

    private static async Task<IResult> ListJobs(
        Guid providerId,
        // Friendly groups (PendingAcceptance | Accepted | InProgress |
        // ModificationRequest | Completed | Cancelled | NoShow | Expired |
        // Upcoming) or raw lifecycle statuses, comma-separated and mixed freely.
        // Those first six partition every status a booking can hold, so a sheet
        // offering them as checkboxes can never lose a job between them.
        string? status,
        // Repeated query values: ?serviceTypes=DayCare&serviceTypes=NightStay.
        string[]? serviceTypes,
        Guid? serviceId,
        // ParentLocation ("Customer Address") | ProviderLocation ("Your Address").
        string? locationType,
        string[]? animalTypes,
        // Case-insensitive "contains" match on the pet's breed.
        string? breed,
        decimal? minEarnings,
        decimal? maxEarnings,
        // A single day is from == to; a stay spanning it is still returned.
        DateOnly? from,
        DateOnly? to,
        string? sortBy,
        string? sortDirection,
        int? skip,
        int? take,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        IProviderJobService jobService,
        CancellationToken cancellationToken)
    {
        var denied = await EnsureCallerOwnsProviderAsync(
            providerId, httpContext, onboardingService, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        ProviderJobQuery query;
        try
        {
            // Every vocabulary is validated before the read, so a typo answers 400
            // rather than silently returning an empty job list the provider would
            // read as "I have no work".
            query = new ProviderJobQuery(
                providerId,
                BookingStatusFilter.Expand(status?.Split(',')),
                ProviderJobQueryParsing.ParseServiceTypes(serviceTypes),
                serviceId,
                ProviderJobQueryParsing.ParseLocationType(locationType),
                ProviderJobQueryParsing.ParseAnimalTypes(animalTypes),
                string.IsNullOrWhiteSpace(breed) ? null : breed.Trim(),
                minEarnings,
                maxEarnings,
                from,
                to,
                ProviderJobQueryParsing.ParseSortBy(sortBy),
                EarningsQueryParsing.ParseSortDirection(sortDirection),
                skip ?? 0,
                take ?? 0);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }

        if (query.From is not null && query.To is not null && query.To < query.From)
        {
            return ApiResults.BadRequest("InvalidRequest", "'to' must be on or after 'from'.");
        }
        if (query.MinEarnings is not null && query.MaxEarnings is not null
            && query.MaxEarnings < query.MinEarnings)
        {
            return ApiResults.BadRequest(
                "InvalidRequest", "'maxEarnings' must be greater than or equal to 'minEarnings'.");
        }

        var page = await jobService.ListAsync(query, cancellationToken);

        return ApiResults.Ok(new ProviderJobsResponse(
            query.Statuses ?? Array.Empty<string>(),
            query.SortBy.ToString(),
            query.SortDirection.ToString(),
            page.TotalCount,
            page.Skip,
            page.Take,
            page.HasMore,
            page.Items.Select(ToResponse).ToArray()));
    }

    /// <summary>
    /// Returns a 403 result when the JWT's provider isn't the one in the route, and
    /// null when the caller may proceed. A caller with no provider profile at all
    /// resolves to null and is therefore also refused — they have no jobs to read.
    /// </summary>
    private static async Task<IResult?> EnsureCallerOwnsProviderAsync(
        Guid providerId,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        Guid? callerProviderId;
        try
        {
            var firebaseUserId = FirebaseClaims.GetFirebaseUserId(httpContext.User);
            var caller = await onboardingService.ResolveProviderByFirebaseUidAsync(
                firebaseUserId, cancellationToken);
            callerProviderId = caller.ProviderId;
        }
        catch (ProviderAuthIdentityForFirebaseUserNotFoundException)
        {
            callerProviderId = null;
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }

        return callerProviderId == providerId
            ? null
            : ApiResults.Forbidden("Forbidden", "You can only view your own jobs.");
    }

    private static ProviderJobResponse ToResponse(ProviderJobRow row) =>
        new(row.BookingType,
            row.BookingId,
            row.JobId,
            row.Status,
            row.IsEarned,
            row.IsPaid,
            row.IsPrivate,
            row.ServiceId,
            row.ServiceType,
            row.ServiceCategory,
            row.SubCategory,
            row.ServiceItemCode,
            row.JobDate,
            row.CheckOutDate,
            row.Nights,
            row.StartTime,
            row.EndTime,
            row.LocationType,
            // Omit the block entirely when the booking froze no address, rather
            // than emitting three nulls the client has to test individually.
            row.AddressLine is null && row.City is null && row.ZipCode is null
                ? null
                : new ProviderJobLocationResponse(row.AddressLine, row.City, row.ZipCode),
            row.JobNotes,
            new ProviderJobCustomerResponse(row.PetParentId, row.CustomerName, row.CustomerPhotoUrl),
            new ProviderJobPetResponse(
                row.PetId, row.PetName, row.AnimalType, row.Breed, row.PetGender),
            row.Amount,
            row.Fee,
            row.PayoutId,
            row.PayoutStatus,
            row.PaidAtUtc,
            row.PaymentMethod,
            row.CreatedAtUtc);
}
