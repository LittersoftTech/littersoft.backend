using Pawfront.Application.ProviderServices;
using Pawfront.Application.Services.PetGroomer;
using Pawfront.Application.Services.PetSitter;
using Pawfront.Application.Services.PetTrainer;
using Pawfront.Application.Services.Vet;
using Pawfront.Domain.Services;

namespace Pawfront.Application.Offerings;

internal sealed class ProviderOfferingResolver(
    IProviderServiceCatalog catalog,
    IPetSitterServiceRegistry petSitter,
    IPetGroomerServiceRegistry petGroomer,
    IPetTrainerServiceRegistry petTrainer,
    IVetServiceRegistry vet) : IProviderOfferingResolver
{
    public async Task<OfferingResolution> ResolveAsync(Guid serviceId, CancellationToken cancellationToken)
    {
        var service = await catalog.GetByIdAsync(serviceId, cancellationToken);
        if (service is null)
        {
            return new OfferingResolution.NotFound();
        }

        if (!service.IsActive)
        {
            return new OfferingResolution.Inactive(service.ProviderId, service.ServiceCategory, service.ServiceType);
        }

        return service.ServiceType switch
        {
            ProviderServiceTypes.DayCare => await ResolvePetSitterAsync(service, includeDayCare: true, cancellationToken),
            ProviderServiceTypes.NightStay => await ResolvePetSitterAsync(service, includeDayCare: false, cancellationToken),
            ProviderServiceTypes.GroomingSession => await ResolvePetGroomerAsync(service, cancellationToken),
            ProviderServiceTypes.TrainingSession => await ResolvePetTrainerAsync(service, cancellationToken),
            ProviderServiceTypes.VetAppointment => await ResolveVetAsync(service, cancellationToken),
            _ => new OfferingResolution.NotConfigured(
                service.ProviderId, service.ServiceCategory, service.SubCategory, service.ServiceType)
        };
    }

    private async Task<OfferingResolution> ResolvePetSitterAsync(
        ProviderService service,
        bool includeDayCare,
        CancellationToken cancellationToken)
    {
        var doc = await petSitter.GetAsync(service.ProviderId, cancellationToken);
        var offering = doc?.PetHotel?.Offering ?? doc?.Freelance?.Offering;
        var branch = includeDayCare ? offering?.DayCare : offering?.NightStay;
        if (offering is null || branch is null)
        {
            return new OfferingResolution.NotConfigured(
                service.ProviderId, service.ServiceCategory, service.SubCategory, service.ServiceType);
        }

        return new OfferingResolution.Resolved(
            service.ServiceId, service.ProviderId,
            service.ServiceCategory, service.SubCategory, service.ServiceType,
            Capacity: offering.MaxPetsAtOneTime,
            DurationHours: branch.MinimumBookingHours,
            IsDurationFixed: false,
            Price: branch.PricePerHour);
    }

    private async Task<OfferingResolution> ResolvePetGroomerAsync(
        ProviderService service,
        CancellationToken cancellationToken)
    {
        var doc = await petGroomer.GetAsync(service.ProviderId, cancellationToken);
        var offering = doc?.GroomerShop?.Offering ?? doc?.Freelance?.Offering;
        if (offering?.Session is null)
        {
            return new OfferingResolution.NotConfigured(
                service.ProviderId, service.ServiceCategory, service.SubCategory, service.ServiceType);
        }

        // Capacity stays shop-wide (all grooming bookings on this provider share
        // the same slot bucket). Duration is per-item — DurationHours is set to 0
        // here as a sentinel; callers MUST follow up with ResolveGroomingItemAsync
        // to get the actual duration for the booking/slot they're computing.
        return new OfferingResolution.Resolved(
            service.ServiceId, service.ProviderId,
            service.ServiceCategory, service.SubCategory, service.ServiceType,
            Capacity: offering.MaxPetsAtOneTime,
            DurationHours: 0m,
            IsDurationFixed: true,
            Price: null);
    }

    public async Task<GroomingItemResolution> ResolveGroomingItemAsync(
        Guid providerId,
        string code,
        CancellationToken cancellationToken)
    {
        var doc = await petGroomer.GetAsync(providerId, cancellationToken);
        var offering = doc?.GroomerShop?.Offering ?? doc?.Freelance?.Offering;
        if (offering?.Session is null || offering.Session.Services.Count == 0)
        {
            return new GroomingItemResolution.OfferingMissing();
        }

        var match = offering.Session.Services.FirstOrDefault(
            s => string.Equals(s.Code, code, StringComparison.Ordinal));
        if (match is null)
        {
            return new GroomingItemResolution.NotOffered(code);
        }

        if (!match.IsActive)
        {
            return new GroomingItemResolution.Inactive(code);
        }

        return new GroomingItemResolution.Resolved(match.Code, match.DurationMinutes, match.Price);
    }

    private async Task<OfferingResolution> ResolvePetTrainerAsync(
        ProviderService service,
        CancellationToken cancellationToken)
    {
        var doc = await petTrainer.GetAsync(service.ProviderId, cancellationToken);
        var offering = doc?.TrainingSchool?.Offering ?? doc?.Freelance?.Offering;
        if (offering?.Session is null)
        {
            return new OfferingResolution.NotConfigured(
                service.ProviderId, service.ServiceCategory, service.SubCategory, service.ServiceType);
        }

        return new OfferingResolution.Resolved(
            service.ServiceId, service.ProviderId,
            service.ServiceCategory, service.SubCategory, service.ServiceType,
            Capacity: offering.MaxConcurrentSessions,
            DurationHours: offering.Session.SessionDurationHours,
            IsDurationFixed: true,
            Price: offering.Session.PricePerSession);
    }

    private async Task<OfferingResolution> ResolveVetAsync(
        ProviderService service,
        CancellationToken cancellationToken)
    {
        var doc = await vet.GetAsync(service.ProviderId, cancellationToken);
        var offering = doc?.VetClinic?.Offering ?? doc?.Freelance?.Offering;
        if (offering?.Appointment is null)
        {
            return new OfferingResolution.NotConfigured(
                service.ProviderId, service.ServiceCategory, service.SubCategory, service.ServiceType);
        }

        return new OfferingResolution.Resolved(
            service.ServiceId, service.ProviderId,
            service.ServiceCategory, service.SubCategory, service.ServiceType,
            Capacity: offering.MaxConcurrentConsultations,
            DurationHours: offering.Appointment.AppointmentDurationHours,
            IsDurationFixed: true,
            Price: offering.Appointment.PricePerAppointment);
    }
}
