namespace AiRaccoon.Core.Memory;

/// <summary>Settings keys for the bank-maintenance background service (checkpoint/vacuum cadence).</summary>
public static class BankMaintenanceConfigKeys
{
    public const string CheckpointIntervalMinutesGlobal = "maintenance.checkpoint-interval-minutes.global";

    public const string VacuumIntervalDaysGlobal = "maintenance.vacuum-interval-days.global";

    public const int DefaultCheckpointIntervalMinutes = 60;

    public const int DefaultVacuumIntervalDays = 7;

    /// <summary>Ceiling for the vacuum interval: TimeSpan.FromDays overflows beyond this.</summary>
    public const int MaxVacuumIntervalDays = 36500;

    public static int ParseCheckpointIntervalMinutes(string? value) => int.TryParse(value, out var minutes) && minutes > 0 ? minutes : DefaultCheckpointIntervalMinutes;

    /// <summary>
    ///     How long a discarded promotion candidate is remembered. A discard means "the agent said
    ///     no, do not propose this again", so it is load-bearing while its entry still exists — the
    ///     purge requires both age and an absent entry (ADR-0055).
    /// </summary>
    public const string PromotionDiscardRetentionDaysGlobal = "maintenance.promotion-discard-retention-days.global";

    public const int DefaultPromotionDiscardRetentionDays = 180;

    /// <summary>How long a search_quality telemetry row is kept.</summary>
    public const string SearchQualityRetentionDaysGlobal = "maintenance.search-quality-retention-days.global";

    public const int DefaultSearchQualityRetentionDays = 90;

    public static int ParsePromotionDiscardRetentionDays(string? value) =>
        int.TryParse(value, out var days) && days > 0 ? days : DefaultPromotionDiscardRetentionDays;

    public static int ParseSearchQualityRetentionDays(string? value) =>
        int.TryParse(value, out var days) && days > 0 ? days : DefaultSearchQualityRetentionDays;

    public static int ParseVacuumIntervalDays(string? value) =>
        int.TryParse(value, out var days) && days > 0
            ? Math.Min(days, MaxVacuumIntervalDays)
            : DefaultVacuumIntervalDays;

    /// <summary>
    ///     Rows drained per signal, for both corpora (WP11-C, owner gate G18) — today's
    ///     4 * EntryEmbedder.BatchSize, unchanged behaviour on day one. Read by
    ///     <see cref="AiRaccoon.Infrastructure.Embedding.EmbedDrainService" /> on every drain pass.
    /// </summary>
    public const string EmbedRowsPerRunGlobal = "maintenance.embed-rows-per-run.global";

    public const int DefaultEmbedRowsPerRun = 128;

    /// <summary>
    ///     True when <paramref name="value" /> is empty (unset) or a positive integer; false only for
    ///     a present value that failed to parse — the caller's cue to warn. <paramref name="rows" />
    ///     is always usable either way.
    /// </summary>
    public static bool TryParseEmbedRowsPerRun(string? value, out int rows)
    {
        if (string.IsNullOrEmpty(value))
        {
            rows = DefaultEmbedRowsPerRun;
            return true;
        }

        if (int.TryParse(value, out var parsed) && parsed > 0)
        {
            rows = parsed;
            return true;
        }

        rows = DefaultEmbedRowsPerRun;
        return false;
    }

    public static int ParseEmbedRowsPerRun(string? value)
    {
        TryParseEmbedRowsPerRun(value, out var rows);
        return rows;
    }
}
