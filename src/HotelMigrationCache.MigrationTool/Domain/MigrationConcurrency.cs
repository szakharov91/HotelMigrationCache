namespace HotelMigrationCache.MigrationTool.Domain;

/// <summary>
/// Уровень параллельности миграции. Значение = количество одновременных воркеров,
/// оно же — размер пула TCP-соединений к кэшу. Максимум ограничен возможностями Cloud API.
/// По умолчанию <see cref="One"/> (последовательный прогон, безопасно для отладки).
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
}
