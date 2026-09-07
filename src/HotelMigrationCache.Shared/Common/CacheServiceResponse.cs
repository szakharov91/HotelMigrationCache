namespace HotelMigrationCache.Shared.Common;

/// <summary>Ответ на команду без полезной нагрузки (SET/DELETE): только статус.</summary>
public record CacheServiceResponse(CacheServiceResponseCode ResponseCode);

/// <summary>Ответ на GET: статус + типизированное значение.
/// <typeparamref name="TValue"/> восстанавливается через static-abstract
/// <c>TValue.DeserializeFromBinary(stream)</c> — источник кода —
/// Roslyn source generator (<c>[GenerateBinarySerializer]</c>).</summary>
public record CacheServiceResponse<TValue>(CacheServiceResponseCode ResponseCode, TValue? Value = default);
