namespace AiRaccoon.Core.Memory;

/// <summary>Settings keys for the bank-maintenance background service (checkpoint/vacuum cadence).</summary>
public static class BankMaintenanceConfigKeys
{
    public const string CheckpointIntervalMinutesGlobal = "maintenance.checkpoint-interval-minutes.global";

    public const string VacuumIntervalDaysGlobal = "maintenance.vacuum-interval-days.global";

    public const int DefaultCheckpointIntervalMinutes = 60;

    public const int DefaultVacuumIntervalDays = 7;

    public static int ParseCheckpointIntervalMinutes(string? value) =>
        int.TryParse(value, out var minutes) && minutes > 0 ? minutes : DefaultCheckpointIntervalMinutes;

    public static int ParseVacuumIntervalDays(string? value) =>
        int.TryParse(value, out var days) && days > 0 ? days : DefaultVacuumIntervalDays;
}
