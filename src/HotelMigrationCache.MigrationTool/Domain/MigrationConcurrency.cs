namespace HotelMigrationCache.MigrationTool.Domain;

/// <summary>
/// Уровень параллельности миграции. Значение = количество одновременных воркеров,
/// оно же — размер пула TCP-соединений к кэшу.
/// <para>
/// <b>Rate-limit контекст</b> (Oracle Hospitality Integration Platform):
/// <list type="bullet">
///   <item>50 rps sustained, 100 rps burst — per gateway, shared по всем consumer'ам.</item>
///   <item>Превышение sustained → автоматические delays на каждый лишний запрос.</item>
///   <item>Превышение burst → HTTP 429 (Too Many Requests).</item>
/// </list>
/// Наш измеренный per-worker cloud rate ≈ 0.63 rps (записи через несколько cloud-вызовов
/// с ~500 мс средней latency). Отсюда безопасные значения:
/// </para>
/// <list type="bullet">
///   <item><see cref="Ten"/> (10) → ~6 rps cloud → 12% budget. Дефолт, консервативно.</item>
///   <item><see cref="TwentyFive"/> (25) → ~16 rps → 32% budget. Разумно на shared gateway.</item>
///   <item><see cref="Sixty"/> (60) → ~38 rps → 76% budget. <b>Boosted-режим</b> — безопасно
///         только когда gateway почти эксклюзивно за миграцией и кэш сокращает cloud calls
///         (иначе легко упереться в 429 на бросках).</item>
/// </list>
/// </summary>
public enum MigrationConcurrency
{
    One = 1,
    Two = 2,
    Three = 3,
    Four = 4,
    Five = 5,
    Six = 6,
    Seven = 7,
    Eight = 8,
    Nine = 9,
    Ten = 10,
    Twenty = 20,
    TwentyFive = 25,
    Forty = 40,
    Sixty = 60,
}
