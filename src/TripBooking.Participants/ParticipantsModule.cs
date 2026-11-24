using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TripBooking.Contracts;

namespace TripBooking.Participants;

public static class ParticipantsModule
{
    /// <summary>Registers the participants this process hosts (all three, or one per container).</summary>
    public static IServiceCollection AddParticipants(
        this IServiceCollection services,
        IConfiguration configuration,
        IReadOnlyCollection<ReservationKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        foreach (var kind in kinds)
        {
            var name = kind.ToString() + "s"; // Flights, Hotels, Cars
            var connectionString = configuration.GetConnectionString(name)
                ?? throw new InvalidOperationException($"Connection string '{name}' is missing.");
            var options = configuration.GetSection($"Participants:{name}").Get<ParticipantOptions>() ?? new ParticipantOptions();
            services.AddKeyedSingleton(kind, (sp, _) => new Participant(
                new ReservationStore(kind, connectionString, options.Capacity, sp.GetRequiredService<TimeProvider>()),
                new ChaosState()));
        }

        return services;
    }

    public static async Task InitializeParticipantsAsync(this IServiceProvider services, IEnumerable<ReservationKind> kinds)
    {
        foreach (var kind in kinds)
        {
            await services.GetRequiredKeyedService<Participant>(kind).Store.InitializeAsync();
        }
    }
}
