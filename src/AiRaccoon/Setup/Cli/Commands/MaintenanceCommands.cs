using System.CommandLine;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>One-shot bank-maintenance verb handlers: interval, vacuum-interval, list — the CLI-only channel for the maintenance service.</summary>
public sealed class MaintenanceCommands : IMaintenanceCommands
{
    public async Task<int> SetCheckpointIntervalAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var minutes = parseResult.GetValue<string>("minutes");
        if (!int.TryParse(minutes, out var parsed) || parsed <= 0)
        {
            await stderr.WriteLineAsync("ai-raccoon: checkpoint interval must be a positive number of minutes");
            return 1;
        }

        await store.SetSettingAsync(BankMaintenanceConfigKeys.CheckpointIntervalMinutesGlobal, parsed.ToString(),
            cancellationToken);
        await stdout.WriteLineAsync($"checkpoint interval: {parsed} min");
        return 0;
    }

    public async Task<int> SetVacuumIntervalAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var days = parseResult.GetValue<string>("days");
        if (!int.TryParse(days, out var parsed) || parsed <= 0)
        {
            await stderr.WriteLineAsync("ai-raccoon: vacuum interval must be a positive number of days");
            return 1;
        }

        await store.SetSettingAsync(BankMaintenanceConfigKeys.VacuumIntervalDaysGlobal, parsed.ToString(),
            cancellationToken);
        await stdout.WriteLineAsync($"vacuum interval: {parsed} days");
        return 0;
    }

    public async Task<int> ListAsync(IMemoryStore store, TextWriter stdout, CancellationToken cancellationToken)
    {
        var checkpoint = BankMaintenanceConfigKeys.ParseCheckpointIntervalMinutes(
            await store.GetSettingAsync(BankMaintenanceConfigKeys.CheckpointIntervalMinutesGlobal, cancellationToken));
        var vacuum = BankMaintenanceConfigKeys.ParseVacuumIntervalDays(
            await store.GetSettingAsync(BankMaintenanceConfigKeys.VacuumIntervalDaysGlobal, cancellationToken));
        await stdout.WriteLineAsync($"checkpoint interval: {checkpoint} min  vacuum interval: {vacuum} days");
        return 0;
    }
}
