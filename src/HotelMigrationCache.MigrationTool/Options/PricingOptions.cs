namespace HotelMigrationCache.MigrationTool.Options;

/// <summary>Ценовые параметры «облачного» API для оценки экономии от кэша.</summary>
public sealed record PricingOptions(
    // Из вендорского прайса: $20 за 10 000 вызовов = $0.002 / вызов.
    decimal CostPer10kCalls = 20m,

    // На rate-limit'ы вендора у нас накладывается retry-каскад. По опыту OHIP/HRS API
    // 20-50% запросов при parallelism=10 без кэша уходят в 429 → retry. Каждый ретрай — тоже billable.
    // Множитель применяется к базовой стоимости для «real-world» оценки.
    decimal ThrottlingOverheadFactor = 1.35m,

    // Продакшн-размер миграции для линейной экстраполяции экономии из тестового прогона.
    int ProjectedRecordCount = 25_000);
