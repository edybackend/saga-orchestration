using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Data.Sqlite;
using TripBooking.Saga.Domain;

namespace TripBooking.Saga.Persistence;

/// <summary>
/// Saga state in the orchestrator's own SQLite database. Saving the state and the new timeline entries is one
/// transaction, so what the API shows always matches what the worker will do next.
/// </summary>
public sealed class SagaStore(string connectionString)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task InitializeAsync()
    {
        var file = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (Path.GetDirectoryName(Path.GetFullPath(file)) is { } directory)
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenAsync();
        await connection.ExecuteAsync("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS trips (
                id              TEXT PRIMARY KEY,
                request         TEXT NOT NULL,   -- JSON
                steps           TEXT NOT NULL,   -- JSON
                status          TEXT NOT NULL,
                attempts        INTEGER NOT NULL,
                next_attempt_at INTEGER NOT NULL, -- unix ms, compared by the worker
                failure_reason  TEXT NULL,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_trips_due ON trips (status, next_attempt_at);
            CREATE TABLE IF NOT EXISTS trip_timeline (
                trip_id TEXT NOT NULL REFERENCES trips (id),
                seq     INTEGER NOT NULL,
                at      TEXT NOT NULL,
                step    TEXT NOT NULL,
                action  TEXT NOT NULL,
                result  TEXT NOT NULL,
                detail  TEXT NOT NULL,
                PRIMARY KEY (trip_id, seq)
            );
            """);
    }

    public async Task SaveAsync(TripSaga saga, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(saga);
        await using var connection = await OpenAsync();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO trips (id, request, steps, status, attempts, next_attempt_at, failure_reason, created_at, updated_at)
                VALUES (@Id, @Request, @Steps, @Status, @Attempts, @NextAttemptAt, @FailureReason, @CreatedAt, @UpdatedAt)
                ON CONFLICT (id) DO UPDATE SET
                    steps = excluded.steps, status = excluded.status, attempts = excluded.attempts,
                    next_attempt_at = excluded.next_attempt_at, failure_reason = excluded.failure_reason, updated_at = excluded.updated_at
                """,
                new
                {
                    Id = saga.Id.ToString(),
                    Request = JsonSerializer.Serialize(saga.Request, Json),
                    Steps = JsonSerializer.Serialize(saga.Steps, Json),
                    Status = saga.Status.ToString(),
                    saga.Attempts,
                    NextAttemptAt = saga.NextAttemptAt.ToUnixTimeMilliseconds(),
                    saga.FailureReason,
                    CreatedAt = saga.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                    UpdatedAt = saga.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
                },
                transaction,
                cancellationToken: ct));

        if (saga.NewEntries.Count > 0)
        {
            var next = await connection.ExecuteScalarAsync<int>(
                new CommandDefinition("SELECT COALESCE(MAX(seq), 0) FROM trip_timeline WHERE trip_id = @id", new { id = saga.Id.ToString() }, transaction, cancellationToken: ct));
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "INSERT INTO trip_timeline (trip_id, seq, at, step, action, result, detail) VALUES (@TripId, @Seq, @At, @Step, @Action, @Result, @Detail)",
                    saga.NewEntries.Select((e, i) => new
                    {
                        TripId = saga.Id.ToString(),
                        Seq = next + i + 1,
                        At = e.At.ToString("O", CultureInfo.InvariantCulture),
                        Step = e.Step.ToString(),
                        Action = e.Action.ToString(),
                        Result = e.Result.ToString(),
                        e.Detail,
                    }),
                    transaction,
                    cancellationToken: ct));
        }

        transaction.Commit();
        saga.MarkEntriesSaved();
    }

    public async Task<TripSaga?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await OpenAsync();
        var row = await connection.QuerySingleOrDefaultAsync<TripRow>(
            new CommandDefinition($"{SelectTrip} WHERE id = @id", new { id = id.ToString() }, cancellationToken: ct));
        return row is null ? null : ToSaga(row);
    }

    /// <summary>Trips the worker has to move forward now: running or compensating, and due.</summary>
    public async Task<IReadOnlyList<TripSaga>> GetDueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        await using var connection = await OpenAsync();
        var rows = await connection.QueryAsync<TripRow>(
            new CommandDefinition(
                $"{SelectTrip} WHERE status IN ('Running', 'Compensating') AND next_attempt_at <= @now ORDER BY next_attempt_at LIMIT @limit",
                new { now = now.ToUnixTimeMilliseconds(), limit },
                cancellationToken: ct));
        return [.. rows.Select(ToSaga)];
    }

    public async Task<IReadOnlyList<TripSaga>> ListAsync(int limit, CancellationToken ct)
    {
        await using var connection = await OpenAsync();
        var rows = await connection.QueryAsync<TripRow>(
            new CommandDefinition($"{SelectTrip} ORDER BY created_at DESC LIMIT @limit", new { limit }, cancellationToken: ct));
        return [.. rows.Select(ToSaga)];
    }

    public async Task<IReadOnlyList<TimelineEntry>> GetTimelineAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await OpenAsync();
        var rows = await connection.QueryAsync<TimelineRow>(
            new CommandDefinition(
                "SELECT at AS At, step AS Step, action AS Action, result AS Result, detail AS Detail FROM trip_timeline WHERE trip_id = @id ORDER BY seq",
                new { id = id.ToString() },
                cancellationToken: ct));
        return [.. rows.Select(r => new TimelineEntry(
            DateTimeOffset.Parse(r.At, CultureInfo.InvariantCulture),
            Enum.Parse<TripBooking.Contracts.ReservationKind>(r.Step),
            Enum.Parse<SagaActionType>(r.Action),
            Enum.Parse<CallResult>(r.Result),
            r.Detail))];
    }

    private const string SelectTrip = """
        SELECT id AS Id, request AS Request, steps AS Steps, status AS Status, attempts AS Attempts,
               next_attempt_at AS NextAttemptAt, failure_reason AS FailureReason, created_at AS CreatedAt, updated_at AS UpdatedAt
        FROM trips
        """;

    private static TripSaga ToSaga(TripRow row) => TripSaga.Restore(
        Guid.Parse(row.Id),
        JsonSerializer.Deserialize<TripRequest>(row.Request, Json)!,
        JsonSerializer.Deserialize<List<SagaStep>>(row.Steps, Json)!,
        Enum.Parse<SagaStatus>(row.Status),
        (int)row.Attempts,
        DateTimeOffset.FromUnixTimeMilliseconds(row.NextAttemptAt),
        row.FailureReason,
        DateTimeOffset.Parse(row.CreatedAt, CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(row.UpdatedAt, CultureInfo.InvariantCulture));

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private sealed record TripRow(
        string Id, string Request, string Steps, string Status, long Attempts, long NextAttemptAt, string? FailureReason, string CreatedAt, string UpdatedAt);

    private sealed record TimelineRow(string At, string Step, string Action, string Result, string Detail);
}
