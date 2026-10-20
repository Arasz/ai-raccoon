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

    public static int ParsePromotionDiscardRetentionDays(string? value) => int.TryParse(value, out var days) && days > 0 ? days : DefaultPromotionDiscardRetentionDays;

    public static int ParseSearchQualityRetentionDays(string? value) => int.TryParse(value, out var days) && days > 0 ? days : DefaultSearchQualityRetentionDays;

    public static int ParseVacuumIntervalDays(string? value) =>
        int.TryParse(value, out var days) && days > 0
            ? Math.Min(days, MaxVacuumIntervalDays)
            : DefaultVacuumIntervalDays;

    /// <summary>
    ///     Rows drained per signal, for both corpora (WP11-C, owner gate G18) — today's
    ///     4 * EntryEmbedder.BatchSize, unchanged behaviour on day one. Read by the embed drain
    ///     (ADR-0091's single consumer) on every drain pass.
    /// </summary>
    public const string EmbedRowsPerRunGlobal = "maintenance.embed-rows-per-run.global";

    public const int DefaultEmbedRowsPerRun = 128;

    /// <summary>
    ///     Floor for embed-rows-per-run: two full embedder sub-batches (2 * BatchSize = 64). Below
    ///     it a drain pass does at most one generator call, and a pass that reaches its budget
    ///     re-signals itself immediately — the per-pass overhead (open the bank, read the setting,
    ///     telemetry) would then outweigh the work the pass does. ADR-0136.
    /// </summary>
    public const int MinEmbedRowsPerRun = 64;

    /// <summary>
    ///     Ceiling for embed-rows-per-run: the drain's one `SELECT ... LIMIT` materialises the whole
    ///     result as a single List before sub-batching at BatchSize (32) — 4096 = 128 * BatchSize
    ///     bounds that one-shot list to a modest burst while leaving generous headroom above the
    ///     128 default. ADR-0136.
    /// </summary>
    public const int MaxEmbedRowsPerRun = 4096;

    /// <summary>
    ///     True when <paramref name="value" /> is empty (unset) or an integer inside
    ///     [<see cref="MinEmbedRowsPerRun" />, <see cref="MaxEmbedRowsPerRun" />]; false for a present
    ///     value that failed to parse, was not positive, or fell outside the range — the caller's
    ///     cue to warn (and, at the CLI, to reject). <paramref name="rows" /> is always usable either
    ///     way: the default for unset/unparseable, the nearest bound for a value outside the range.
    /// </summary>
    public static bool TryParseEmbedRowsPerRun(string? value, out int rows)
    {
        if (string.IsNullOrEmpty(value))
        {
            rows = DefaultEmbedRowsPerRun;
            return true;
        }

        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            rows = DefaultEmbedRowsPerRun;
            return false;
        }

        if (parsed < MinEmbedRowsPerRun)
        {
            rows = MinEmbedRowsPerRun;
            return false;
        }

        if (parsed > MaxEmbedRowsPerRun)
        {
            rows = MaxEmbedRowsPerRun;
            return false;
        }

        rows = parsed;
        return true;
    }

    public static int ParseEmbedRowsPerRun(string? value)
    {
        TryParseEmbedRowsPerRun(value, out var rows);
        return rows;
    }
}
