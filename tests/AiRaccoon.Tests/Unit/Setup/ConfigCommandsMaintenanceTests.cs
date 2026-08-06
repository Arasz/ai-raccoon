using AiRaccoon.Core.Memory;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Maintenance-config commands pin the settings-key contract the bank-maintenance
///     hosted service reads: maintenance.checkpoint-interval-minutes.global (default 60)
///     and maintenance.vacuum-interval-days.global (default 7).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ConfigCommandsMaintenanceTests
{
    private static async Task<(int Exit, string Out, string Err)> Run(string[] args, FakeConfigStore store)
    {
        CliArgs.TryParse(args, out var parsed);
        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldNotBeEmpty();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await ConfigCommands.RunAsync(parsed.CommandPath, parsed.ParseResult, store, stdout, stderr,
            TextReader.Null, maintenance: new MaintenanceCommands(),
            cancellationToken: TestContext.Current.CancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task MaintenanceIntervalSet_WritesGlobalRow()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["maintenance", "interval", "60"], store);

        exit.ShouldBe(0);
        store.Settings[BankMaintenanceConfigKeys.CheckpointIntervalMinutesGlobal].ShouldBe("60");
        outp.ShouldContain("checkpoint interval: 60 min");
    }

    [Fact]
    public async Task MaintenanceIntervalInvalid_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["maintenance", "interval", "0"], store);

        exit.ShouldBe(1);
        err.ShouldContain("positive number of minutes");
        store.Settings.ShouldNotContainKey(BankMaintenanceConfigKeys.CheckpointIntervalMinutesGlobal);
    }

    [Fact]
    public async Task MaintenanceIntervalNonNumeric_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["maintenance", "interval", "often"], store);

        exit.ShouldBe(1);
        err.ShouldContain("positive number of minutes");
        store.Settings.ShouldNotContainKey(BankMaintenanceConfigKeys.CheckpointIntervalMinutesGlobal);
    }

    [Fact]
    public async Task MaintenanceVacuumIntervalSet_WritesGlobalRow()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["maintenance", "vacuum-interval", "7"], store);

        exit.ShouldBe(0);
        store.Settings[BankMaintenanceConfigKeys.VacuumIntervalDaysGlobal].ShouldBe("7");
        outp.ShouldContain("vacuum interval: 7 days");
    }

    [Fact]
    public async Task MaintenanceVacuumIntervalInvalid_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["maintenance", "vacuum-interval", "-1"], store);

        exit.ShouldBe(1);
        err.ShouldContain("positive number of days");
        store.Settings.ShouldNotContainKey(BankMaintenanceConfigKeys.VacuumIntervalDaysGlobal);
    }

    [Fact]
    public async Task MaintenanceList_ShowsDefaults_WhenUnset()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["maintenance", "list"], store);

        exit.ShouldBe(0);
        outp.ShouldContain("checkpoint interval: 60 min");
        outp.ShouldContain("vacuum interval: 7 days");
    }

    [Fact]
    public async Task MaintenanceList_ShowsConfiguredValues()
    {
        var store = new FakeConfigStore();
        store.Settings[BankMaintenanceConfigKeys.CheckpointIntervalMinutesGlobal] = "30";
        store.Settings[BankMaintenanceConfigKeys.VacuumIntervalDaysGlobal] = "3";

        var (exit, outp, _) = await Run(["maintenance", "list"], store);

        exit.ShouldBe(0);
        outp.ShouldContain("checkpoint interval: 30 min");
        outp.ShouldContain("vacuum interval: 3 days");
    }
}
