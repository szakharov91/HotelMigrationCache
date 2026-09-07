using HotelMigrationCache.MigrationTool.Domain;

namespace HotelMigrationCache.MigrationTool.Contracts;

// Локальное состояние миграции в SQLite: строки таблиц Profiles и Reservations.
public interface IMigrationRepository
{
    Task InitializeSchemaAsync(CancellationToken ct);

    // Одна транзакция на батч — иначе 25000 индивидуальных инсертов работают минутами.
    // Семантика: ON CONFLICT(SrcId) DO NOTHING — Init идемпотентен.
    Task InsertProfilesIfMissingAsync(IReadOnlyCollection<ProfileRecord> records, CancellationToken ct);
    Task InsertReservationsIfMissingAsync(IReadOnlyCollection<ReservationRecord> records, CancellationToken ct);

    Task UpdateProfileStateAsync(string srcId, MigrationState state, string? dstId, string? error, CancellationToken ct);
    Task<ProfileRecord?> GetProfileBySrcIdAsync(string srcId, CancellationToken ct);
    IAsyncEnumerable<ProfileRecord> GetPendingProfilesAsync(CancellationToken ct);
    Task<MigrationCounters> GetProfileCountersAsync(CancellationToken ct);

    Task UpdateReservationStateAsync(string srcId, MigrationState state, string? dstId, string? mainProfileDstId, string? error, CancellationToken ct);
    IAsyncEnumerable<ReservationRecord> GetPendingReservationsAsync(CancellationToken ct);
    Task<MigrationCounters> GetReservationCountersAsync(CancellationToken ct);
}

public sealed record MigrationCounters(long Total, long New, long Processing, long Success, long Failed);
