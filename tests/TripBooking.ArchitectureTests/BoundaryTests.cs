using System.Reflection;
using NetArchTest.Rules;

namespace TripBooking.ArchitectureTests;

/// <summary>The orchestrator and the participants meet only at the HTTP contract, and the saga logic stays pure.</summary>
public class BoundaryTests
{
    private static readonly Assembly SagaAssembly = typeof(TripBooking.Saga.SagaWorker).Assembly;
    private static readonly Assembly ParticipantsAssembly = typeof(TripBooking.Participants.ReservationStore).Assembly;
    private static readonly Assembly ContractsAssembly = typeof(TripBooking.Contracts.ReserveRequest).Assembly;

    [Fact]
    public void The_orchestrator_never_reaches_into_a_participant() =>
        AssertNoDependency(Types.InAssembly(SagaAssembly), "TripBooking.Participants");

    [Fact]
    public void Participants_know_nothing_about_the_saga() =>
        AssertNoDependency(Types.InAssembly(ParticipantsAssembly), "TripBooking.Saga");

    [Fact]
    public void The_contract_depends_on_nothing_of_ours() =>
        AssertNoDependency(Types.InAssembly(ContractsAssembly), "TripBooking.Saga", "TripBooking.Participants", "Microsoft.AspNetCore", "Dapper");

    [Fact]
    public void The_saga_state_machine_does_no_io() =>
        AssertNoDependency(
            Types.InAssembly(SagaAssembly).That().ResideInNamespace("TripBooking.Saga.Domain"),
            "TripBooking.Saga.Persistence", "TripBooking.Saga.Participants", "Dapper", "Microsoft.Data.Sqlite", "System.Net.Http", "Microsoft.AspNetCore", "Microsoft.Extensions");

    private static void AssertNoDependency(Types types, params string[] forbidden) =>
        AssertNoDependency(types.That().HaveNameMatching(".*"), forbidden);

    private static void AssertNoDependency(PredicateList types, params string[] forbidden)
    {
        var result = types.ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

        Assert.True(result.IsSuccessful, "Violations: " + string.Join(", ", result.FailingTypeNames ?? []));
    }
}
