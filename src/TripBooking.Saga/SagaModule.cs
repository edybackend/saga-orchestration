using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TripBooking.Contracts;
using TripBooking.Saga.Participants;
using TripBooking.Saga.Persistence;

namespace TripBooking.Saga;

public static class SagaModule
{
    public static IServiceCollection AddTripSaga(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Trips")
            ?? throw new InvalidOperationException("Connection string 'Trips' is missing.");
        services.Configure<SagaOptions>(configuration.GetSection(SagaOptions.SectionName));
        services.AddSingleton(new SagaStore(connectionString));
        services.AddSingleton<TripService>();
        services.AddSingleton<IParticipantClient, HttpParticipantClient>();

        foreach (var kind in Enum.GetValues<ReservationKind>())
        {
            services.AddHttpClient(HttpParticipantClient.ClientName(kind), (sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<SagaOptions>>().Value;
                var name = kind + "s";
                client.BaseAddress = options.Participants.TryGetValue(name, out var url)
                    ? url
                    : throw new InvalidOperationException($"Saga:Participants:{name} (base URL) is missing.");
                client.Timeout = options.CallTimeout;
            });
        }

        services.AddSingleton<SagaWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<SagaWorker>());
        return services;
    }
}
