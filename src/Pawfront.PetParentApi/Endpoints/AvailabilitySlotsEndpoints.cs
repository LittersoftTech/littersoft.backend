using Pawfront.Application.Availability;
using Pawfront.Contracts.Availability;

namespace Pawfront.PetParentApi.Endpoints;

/// <summary>
/// Parent-facing free-slots endpoint. Mirrors the provider host's
/// <c>GET /providers/{providerId}/availability/slots</c> handler — same
/// shared <see cref="IProviderAvailabilitySlotService"/> backing, same
/// per-category duration rules, same race-safe capacity logic. Duplicated
/// here because the two hosts authenticate against different Firebase
/// projects.
///
/// The provider's save/get weekly-hours endpoints (POST / and GET / under
/// the same group) are intentionally NOT mirrored — those are organiser-
/// only. The 7-day working hours are already surfaced on the parent host
/// via <c>GET /providers/{providerId}</c> (under <c>workingHours</c>).
/// </summary>
internal static class AvailabilitySlotsEndpoints
{
    private const int DefaultGranularityMinutes = 30;

    public static IEndpointRouteBuilder MapAvailabilitySlotsEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapGet("/providers/{providerId:guid}/availability/slots", GetAvailableSlots);
        return builder;
    }

    private static async Task<IResult> GetAvailableSlots(
        Guid providerId,
        Guid serviceId,
        DateOnly date,
        decimal? durationHours,
        int? granularityMinutes,
        string? serviceItemCode,
        IProviderAvailabilitySlotService slotService,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await slotService.GetAvailableSlotsAsync(
                providerId,
                serviceId,
                date,
                durationHours ?? 0m,
                granularityMinutes ?? DefaultGranularityMinutes,
                serviceItemCode,
                cancellationToken);

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
        catch (SlotGroomingItemCodeRequiredException exception)
        {
            return ApiResults.BadRequest("ServiceItemCodeRequired", exception.Message);
        }
        catch (SlotGroomingItemNotOfferedException exception)
        {
            return ApiResults.BadRequest("ServiceItemNotOffered", exception.Message);
        }
        catch (SlotGroomingItemInactiveException exception)
        {
            return ApiResults.Conflict("ServiceItemInactive", exception.Message);
        }
        catch (InvalidBookingDurationException exception)
        {
            return ApiResults.BadRequest("InvalidBookingDuration", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static AvailableSlotsResponse ToResponse(AvailableSlotsResult result)
    {
        return new AvailableSlotsResponse(
            result.ProviderId,
            result.ServiceId,
            result.Date,
            result.ServiceCategory,
            result.SubCategory,
            result.ServiceType,
            result.DurationHours,
            result.Capacity,
            result.GranularityMinutes,
            result.Slots.Select(s => new TimeSlotResponse(s.StartTime, s.EndTime)).ToArray());
    }
}
