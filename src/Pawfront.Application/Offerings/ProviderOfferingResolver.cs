using Pawfront.Application.Services.PetGroomer;
using Pawfront.Application.Services.PetSitter;
using Pawfront.Application.Services.PetTrainer;
using Pawfront.Application.Services.ProviderServiceLocations;
using Pawfront.Application.Services.Vet;
using Pawfront.Domain.Services;

namespace Pawfront.Application.Offerings;

internal sealed class ProviderOfferingResolver(
    IProviderServiceLocationRegistry locationRegistry,
    IPetSitterServiceRegistry petSitter,
    IPetGroomerServiceRegistry petGroomer,
    IPetTrainerServiceRegistry petTrainer,
    IVetServiceRegistry vet) : IProviderOfferingResolver
{
    public async Task<OfferingResolution> ResolveAsync(Guid providerId, CancellationToken cancellationToken)
    {
        var registration = await locationRegistry.GetByProviderIdAsync(providerId, cancellationToken);
        if (registration is null)
        {
            return new OfferingResolution.NotRegistered();
        }

        var category = registration.ServiceCategory;
        var subCategory = registration.SubCategory;

        if (category == nameof(ProviderServiceCategory.PetSitter))
        {
            var doc = await petSitter.GetAsync(providerId, cancellationToken);
            var offering = doc?.PetHotel?.Offering ?? doc?.Freelance?.Offering;
            if (offering is null) return new OfferingResolution.NotConfigured(category, subCategory);

            var minHours = MinAvailable(
                offering.DayCare?.MinimumBookingHours,
                offering.NightStay?.MinimumBookingHours);
            if (minHours is null) return new OfferingResolution.NotConfigured(category, subCategory);

            return new OfferingResolution.Resolved(
                category, subCategory, offering.MaxPetsAtOneTime, minHours.Value, IsDurationFixed: false);
        }

        if (category == nameof(ProviderServiceCategory.PetGroomer))
        {
            var doc = await petGroomer.GetAsync(providerId, cancellationToken);
            var offering = doc?.GroomerShop?.Offering ?? doc?.Freelance?.Offering;
            if (offering?.Session is null) return new OfferingResolution.NotConfigured(category, subCategory);

            return new OfferingResolution.Resolved(
                category, subCategory, offering.MaxPetsAtOneTime,
                offering.Session.MinimumBookingHours, IsDurationFixed: false);
        }

        if (category == nameof(ProviderServiceCategory.PetTrainer))
        {
            var doc = await petTrainer.GetAsync(providerId, cancellationToken);
            var offering = doc?.TrainingSchool?.Offering ?? doc?.Freelance?.Offering;
            if (offering?.Session is null) return new OfferingResolution.NotConfigured(category, subCategory);

            return new OfferingResolution.Resolved(
                category, subCategory, offering.MaxConcurrentSessions,
                offering.Session.SessionDurationHours, IsDurationFixed: true);
        }

        if (category == nameof(ProviderServiceCategory.Vet))
        {
            var doc = await vet.GetAsync(providerId, cancellationToken);
            var offering = doc?.VetClinic?.Offering ?? doc?.Freelance?.Offering;
            if (offering?.Appointment is null) return new OfferingResolution.NotConfigured(category, subCategory);

            return new OfferingResolution.Resolved(
                category, subCategory, offering.MaxConcurrentConsultations,
                offering.Appointment.AppointmentDurationHours, IsDurationFixed: true);
        }

        // Pet Adoption & Sale has no offering structure yet.
        return new OfferingResolution.NotConfigured(category, subCategory);
    }

    private static decimal? MinAvailable(int? a, int? b)
    {
        if (a is null && b is null) return null;
        if (a is null) return b!.Value;
        if (b is null) return a.Value;
        return Math.Min(a.Value, b.Value);
    }
}
