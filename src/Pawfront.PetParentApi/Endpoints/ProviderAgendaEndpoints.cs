using Pawfront.Application.Availability;
using Pawfront.Contracts.Availability;
using Pawfront.PetParentApi.Auth;

namespace Pawfront.PetParentApi.Endpoints;

/// <summary>
/// Parent-facing daily agenda for one of a provider's services: the day laid out
/// as a timeline of booked and free blocks, so the parent can see the shape of
/// the day and pick a slot.
///
/// The sibling of <c>GET /providers/{providerId}/availability/slots</c> — same
/// working hours, closures, capacity and bookings underneath — but it needs no
/// duration up front, which the slots endpoint does. That's the point: the parent
/// browses the agenda first, then asks for slots once they know what they want.
///
/// Lives outside the ownership-filtered groups (like the rest of
/// <c>/providers/*</c>): the caller is looking at somebody else's calendar. Their
/// own PetParentId is still resolved from the JWT — it decides which blocks
/// reveal a real job status + id and which are masked to a bare "BOOKED".
/// </summary>
internal static class ProviderAgendaEndpoints
{
    public static IEndpointRouteBuilder MapProviderAgendaEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapGet("/providers/{providerId:guid}/agenda", GetDailyAgenda);
        return builder;
    }

    private static async Task<IResult> GetDailyAgenda(
        Guid providerId,
        Guid serviceId,
        DateOnly date,
        IProviderDailyAgendaService agendaService,
        ICurrentPetParentContext currentPetParent,
        CancellationToken cancellationToken)
    {
        try
        {
            // Never taken from the route or query — a caller can only ever be
            // shown their own jobs unmasked.
            var callerPetParentId = await currentPetParent.GetPetParentIdAsync(cancellationToken);

            var result = await agendaService.GetDailyAgendaAsync(
                providerId, serviceId, date, callerPetParentId, cancellationToken);

            return ApiResults.Ok(ToResponse(result));
        }
        catch (SlotServiceInvalidException exception)
        {
            return ApiResults.BadRequest("InvalidServiceId", exception.Message);
        }
        catch (ProviderServiceNotRegisteredException exception)
        {
            return ApiResults.NotFound("ServiceNotRegistered", exception.Message);
        }
        catch (ProviderOfferingNotConfiguredException exception)
        {
            return ApiResults.BadRequest("OfferingNotConfigured", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static ProviderDailyAgendaResponse ToResponse(ProviderDailyAgendaResult result) =>
        new(result.ProviderId,
            result.ServiceId,
            result.Date,
            result.ServiceCategory,
            result.SubCategory,
            result.ServiceType,
            result.Capacity,
            result.IsOpen,
            result.IsClosedForDay,
            result.OpeningTime,
            result.ClosingTime,
            result.Entries
                .Select(e => new AgendaEntryResponse(
                    e.StartTime,
                    e.EndTime,
                    e.EntryType.ToString(),
                    e.Status,
                    e.JobId,
                    e.BookingId,
                    e.RemainingCapacity,
                    e.IsBookable))
                .ToArray());
}
