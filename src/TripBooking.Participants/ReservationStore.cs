using Dapper;
using Microsoft.Data.Sqlite;
using TripBooking.Contracts;

namespace TripBooking.Participants;

public enum ReserveResult
{
    Reserved,
    SoldOut,
    CancelledBefore,
}

public sealed record ReserveOutcome(ReserveResult Result, ReservationResponse? Reservation);

/// <summary>
/// One participant's reservations in its own SQLite database. Every write runs in a <c>BEGIN IMMEDIATE</c>
/// transaction, so the capacity check and the insert cannot interleave with another request.
/// </summary>
public sealed class ReservationStore(ReservationKind kind, string connectionString, int capacity, TimeProvider clock)
{
    private const string Reserved = "Reserved";
    private const string Cancelled = "Cancelled";

    public ReservationKind Kind => kind;

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
            CREATE TABLE IF NOT EXISTS reservations (
                id             TEXT PRIMARY KEY,
                resource_key   TEXT NULL,      -- NULL for a tombstone: cancelled before it was ever reserved
                customer_email TEXT NULL,
                status         TEXT NOT NULL,
                updated_at     TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_reservations_resource ON reservations (resource_key, status);
            """);
    }

    /// <summary>Idempotent: the same id returns the existing reservation and uses no extra unit.</summary>
    public async Task<ReserveOutcome> ReserveAsync(string reservationId, ReserveRequest request, CancellationToken ct)
    {
        await using var connection = await OpenAsync();
        await using var transaction = connection.BeginTransaction(deferred: false); // BEGIN IMMEDIATE
        var existing = await FindAsync(connection, transaction, reservationId);
        if (existing is not null)
        {
            return existing.Status == Reserved
                ? new ReserveOutcome(ReserveResult.Reserved, existing)
                : new ReserveOutcome(ReserveResult.CancelledBefore, existing);
        }

        var reserved = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM reservations WHERE resource_key = @key AND status = 'Reserved'",
                new { key = request.ResourceKey },
                transaction,
                cancellationToken: ct));
        if (reserved >= capacity)
        {
            return new ReserveOutcome(ReserveResult.SoldOut, null);
        }

        var now = clock.GetUtcNow();
        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO reservations (id, resource_key, customer_email, status, updated_at) VALUES (@id, @key, @email, 'Reserved', @now)",
                new { id = reservationId, key = request.ResourceKey, email = request.CustomerEmail, now = now.ToString("O") },
                transaction,
                cancellationToken: ct));
        transaction.Commit();
        return new ReserveOutcome(ReserveResult.Reserved, new ReservationResponse(reservationId, kind, request.ResourceKey, Reserved, now));
    }

    /// <summary>
    /// Idempotent. Cancelling an id that was never reserved writes a tombstone, so a reserve that was still in
    /// flight when the saga gave up is refused when it finally arrives.
    /// </summary>
    public async Task<ReservationResponse> CancelAsync(string reservationId, CancellationToken ct)
    {
        await using var connection = await OpenAsync();
        await using var transaction = connection.BeginTransaction(deferred: false);
        var now = clock.GetUtcNow();
        var existing = await FindAsync(connection, transaction, reservationId);
        if (existing is { Status: Cancelled })
        {
            return existing;
        }

        await connection.ExecuteAsync(
            new CommandDefinition(
                existing is null
                    ? "INSERT INTO reservations (id, status, updated_at) VALUES (@id, 'Cancelled', @now)"
                    : "UPDATE reservations SET status = 'Cancelled', updated_at = @now WHERE id = @id",
                new { id = reservationId, now = now.ToString("O") },
                transaction,
                cancellationToken: ct));
        transaction.Commit();
        return new ReservationResponse(reservationId, kind, existing?.ResourceKey, Cancelled, now);
    }

    public async Task<ReservationResponse?> GetAsync(string reservationId)
    {
        await using var connection = await OpenAsync();
        return await FindAsync(connection, null, reservationId);
    }

    public async Task<AvailabilityResponse> GetAvailabilityAsync(string resourceKey)
    {
        await using var connection = await OpenAsync();
        var reserved = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM reservations WHERE resource_key = @resourceKey AND status = 'Reserved'", new { resourceKey });
        return new AvailabilityResponse(kind, resourceKey, capacity, reserved, Math.Max(0, capacity - reserved));
    }

    private async Task<ReservationResponse?> FindAsync(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ReservationRow>(
            "SELECT id AS Id, resource_key AS ResourceKey, status AS Status, updated_at AS UpdatedAt FROM reservations WHERE id = @id",
            new { id },
            transaction);
        return row is null ? null : new ReservationResponse(row.Id, kind, row.ResourceKey, row.Status, DateTimeOffset.Parse(row.UpdatedAt, System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private sealed record ReservationRow(string Id, string? ResourceKey, string Status, string UpdatedAt);
}
