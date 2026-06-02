using Pawfront.Application.Availability;
using Pawfront.Contracts.Availability;

namespace Pawfront.Api.Endpoints;

internal static class ProviderAvailabilityEndpoints
{
    private const int DefaultGranularityMinutes = 30;

    public static IEndpointRouteBuilder MapProviderAvailabilityEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/providers/{providerId:guid}/availability");

        group.MapPost("/", SaveAvailability);
        group.MapGet("/", GetAvailability);
        group.MapGet("/slots", GetAvailableSlots);

        return builder;
    }

    private static async Task<IResult> SaveAvailability(
        Guid providerId,
        SaveProviderWeeklyAvailabilityRequest request,
        IProviderAvailabilityService availabilityService,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await availabilityService.SaveAsync(
                new SaveProviderWeeklyAvailabilityCommand(
                    providerId,
                    request.Days
                        .Select(d => new DayAvailabilityInput(
                            d.DayOfWeek, d.IsOpen, d.StartTime, d.EndTime, d.BreakStartTime, d.BreakEndTime))
                        .ToList()),
                cancellationToken);

            return ApiResults.Ok(ToResponse(result));
        }
        catch (AvailabilityProviderNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderProfileNotFound", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> GetAvailability(
        Guid providerId,
        IProviderAvailabilityService availabilityService,
        CancellationToken cancellationToken)
    {
        var result = await availabilityService.GetAsync(providerId, cancellationToken);
        return ApiResults.Ok(ToResponse(result));
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

    private static ProviderWeeklyAvailabilityResponse ToResponse(ProviderWeeklyAvailabilityResult result)
    {
        return new ProviderWeeklyAvailabilityResponse(
            result.ProviderId,
            result.Days
                .Select(d => new DayAvailabilityResponse(
                    d.DayOfWeek, d.IsOpen, d.StartTime, d.EndTime, d.BreakStartTime, d.BreakEndTime))
                .ToList());
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
