using System.Text.RegularExpressions;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Setup;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     Maintenance-config commands pin the settings-key contract the bank-maintenance hosted
///     service reads (checkpoint interval, vacuum interval, embed rows per run); the list verb also reports live
///     bank disk stats with delta-vs-previous via the maintenance-stats.json sidecar.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ConfigCommandsMaintenanceTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("maintenance-cli");
    private readonly SqliteConnectionFactory _factory;

    public ConfigCommandsMaintenanceTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private Task<(int Exit, string Out, string Err)> Run(string[] args, FakeConfigStore store) =>
        CliRun.RunAsync(args, TestData.CreateConfigCommands(store,
            maintenance: new MaintenanceCommands(new SqliteMaintenanceStatsStore(_factory), TestData.CreateInfrastructureOptions(_dataRoot))));

    /// <summary>Writes then deletes rows so the bank's freelist grows.</summary>
    private async Task SeedFreelistAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
                             INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                             VALUES (@hash, @path, @value, 'project', 'acme', 0, 0)
                             """;
        var hash = insert.Parameters.Add("@hash", SqliteType.Text);
        var path = insert.Parameters.Add("@path", SqliteType.Text);
        var value = insert.Parameters.Add("@value", SqliteType.Text);
        for (var i = 0; i < 2000; i++)
        {
            hash.Value = $"h{i}";
            path.Value = $"p{i}.md";
            value.Value = new string('x', 200);
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM entries WHERE project_id = 'acme'";
        await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>One committed write so the bank grows between two list calls.</summary>
    private async Task WriteRowAsync(string suffix)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                              VALUES (@hash, @path, @value, 'project', 'acme', 0, 0)
                              """;
        command.Parameters.AddWithValue("@hash", $"delta-{suffix}");
        command.Parameters.AddWithValue("@path", $"delta-{suffix}.md");
        command.Parameters.AddWithValue("@value", new string('y', 400));
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [RetryFact]
    public async Task MaintenanceIntervalSet_WritesGlobalRow()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["settings", "maintenance", "interval", "60"], store);

        exit.ShouldBe(0);
        store.Settings[BankMaintenanceConfigKeys.CheckpointIntervalMinutesGlobal].ShouldBe("60");
        outp.ShouldContain("checkpoint interval: 60 min");
    }

    [RetryFact]
    public async Task MaintenanceIntervalInvalid_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["settings", "maintenance", "interval", "0"], store);

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("positive number of minutes");
        store.Settings.ShouldNotContainKey(BankMaintenanceConfigKeys.CheckpointIntervalMinutesGlobal);
    }

    [RetryFact]
    public async Task MaintenanceIntervalNonNumeric_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["settings", "maintenance", "interval", "often"], store);

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("positive number of minutes");
        store.Settings.ShouldNotContainKey(BankMaintenanceConfigKeys.CheckpointIntervalMinutesGlobal);
    }

    [RetryFact]
    public async Task MaintenanceVacuumIntervalSet_WritesGlobalRow()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["settings", "maintenance", "vacuum-interval", "7"], store);

        exit.ShouldBe(0);
        store.Settings[BankMaintenanceConfigKeys.VacuumIntervalDaysGlobal].ShouldBe("7");
        outp.ShouldContain("vacuum interval: 7 days");
    }

    [RetryFact]
    public async Task MaintenanceVacuumIntervalInvalid_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["settings", "maintenance", "vacuum-interval", "-1"], store);

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("positive number of days");
        store.Settings.ShouldNotContainKey(BankMaintenanceConfigKeys.VacuumIntervalDaysGlobal);
    }

    [RetryFact]
    public async Task MaintenanceVacuumIntervalOutOfRange_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["settings", "maintenance", "vacuum-interval", "20000000"], store);

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("days");
        store.Settings.ShouldNotContainKey(BankMaintenanceConfigKeys.VacuumIntervalDaysGlobal);
    }

    [RetryFact]
    public async Task MaintenanceList_ShowsDefaults_WhenUnset()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["settings", "maintenance", "list"], store);

        exit.ShouldBe(0);
        outp.ShouldContain("checkpoint interval: 60 min");
        outp.ShouldContain("vacuum interval: 7 days");
    }

    [RetryFact]
    public async Task MaintenanceList_ShowsDiskStats_AndReclaimable()
    {
        var store = new FakeConfigStore();
        await SeedFreelistAsync();

        var (exit, outp, _) = await Run(["settings", "maintenance", "list"], store);

        exit.ShouldBe(0);
        outp.ShouldContain("db file:");
        outp.ShouldContain("on-disk total:");
        outp.ShouldContain("reclaimable:");
        outp.ShouldContain("freelist");
        // Decimal separator must be invariant (a locale with comma decimals would break parsing).
        Regex.IsMatch(outp, @"\d+\.\d+ MB").ShouldBeTrue();
    }

    [RetryFact]
    public async Task MaintenanceList_SecondCall_ShowsDelta()
    {
        var store = new FakeConfigStore();
        var (exit1, outp1, _) = await Run(["settings", "maintenance", "list"], store);
        exit1.ShouldBe(0);
        outp1.ShouldContain("no previous measurement");

        await WriteRowAsync("probe");

        var (exit2, outp2, _) = await Run(["settings", "maintenance", "list"], store);
        exit2.ShouldBe(0);
        outp2.ShouldContain("since last check:");
    }

    [RetryFact]
    public async Task MaintenanceList_WritesStatsSidecar()
    {
        var store = new FakeConfigStore();

        await Run(["settings", "maintenance", "list"], store);

        File.Exists(Path.Combine(_dataRoot, "maintenance-stats.json")).ShouldBeTrue();
    }

    [RetryFact]
    public async Task MaintenanceList_ShowsConfiguredValues()
    {
        var store = new FakeConfigStore();
        store.Settings[BankMaintenanceConfigKeys.CheckpointIntervalMinutesGlobal] = "30";
        store.Settings[BankMaintenanceConfigKeys.VacuumIntervalDaysGlobal] = "3";

        var (exit, outp, _) = await Run(["settings", "maintenance", "list"], store);

        exit.ShouldBe(0);
        outp.ShouldContain("checkpoint interval: 30 min");
        outp.ShouldContain("vacuum interval: 3 days");
    }

    /// <summary>WP11-C (G18): the "cheaper first move" the owner can turn without a release.</summary>
    [RetryFact]
    public async Task MaintenanceEmbedRowsPerRunSet_WritesGlobalRow()
    {
        var store = new FakeConfigStore();

        var (exit, outp, _) = await Run(["settings", "maintenance", "embed-rows-per-run", "512"], store);

        exit.ShouldBe(0);
        store.Settings[BankMaintenanceConfigKeys.EmbedRowsPerRunGlobal].ShouldBe("512");
        outp.ShouldContain("embed rows per run: 512");
    }

    [RetryFact]
    public async Task MaintenanceEmbedRowsPerRunInvalid_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["settings", "maintenance", "embed-rows-per-run", "0"], store);

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("positive");
        store.Settings.ShouldNotContainKey(BankMaintenanceConfigKeys.EmbedRowsPerRunGlobal);
    }

    [RetryFact]
    public async Task MaintenanceEmbedRowsPerRunNonNumeric_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["settings", "maintenance", "embed-rows-per-run", "many"], store);

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("positive");
        store.Settings.ShouldNotContainKey(BankMaintenanceConfigKeys.EmbedRowsPerRunGlobal);
    }

    [RetryFact]
    public async Task MaintenanceEmbedRowsPerRunEmpty_Returns1_AndWritesNothing()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["settings", "maintenance", "embed-rows-per-run", ""], store);

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("positive");
        store.Settings.ShouldNotContainKey(BankMaintenanceConfigKeys.EmbedRowsPerRunGlobal);
    }

    /// <summary>
    ///     Review finding 1 (#517), BLOCKING: an unbounded rows-per-run lets `EntryEmbedder`/
    ///     `CodeEmbedder` materialise the whole `SELECT ... LIMIT` result as one List — exactly the
    ///     burst this setting exists to prevent. The CLI rejects rather than silently clamping,
    ///     mirroring `settings maintenance vacuum-interval`'s ceiling guard.
    /// </summary>
    [RetryFact]
    public async Task MaintenanceEmbedRowsPerRunOverCeiling_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["settings", "maintenance", "embed-rows-per-run", "2000000000"], store);

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain(BankMaintenanceConfigKeys.MaxEmbedRowsPerRun.ToString());
        store.Settings.ShouldNotContainKey(BankMaintenanceConfigKeys.EmbedRowsPerRunGlobal);
    }

    [RetryFact]
    public async Task MaintenanceEmbedRowsPerRunAtTheCeiling_WritesGlobalRow()
    {
        var store = new FakeConfigStore();
        var ceiling = BankMaintenanceConfigKeys.MaxEmbedRowsPerRun.ToString();

        var (exit, outp, _) = await Run(["settings", "maintenance", "embed-rows-per-run", ceiling], store);

        exit.ShouldBe(0);
        store.Settings[BankMaintenanceConfigKeys.EmbedRowsPerRunGlobal].ShouldBe(ceiling);
        outp.ShouldContain($"embed rows per run: {ceiling}");
    }

    [RetryFact]
    public async Task MaintenanceList_ShowsEmbedRowsPerRun_DefaultAndConfigured()
    {
        var store = new FakeConfigStore();

        var (exitDefault, outpDefault, _) = await Run(["settings", "maintenance", "list"], store);
        exitDefault.ShouldBe(0);
        outpDefault.ShouldContain($"embed rows per run: {BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun}");

        store.Settings[BankMaintenanceConfigKeys.EmbedRowsPerRunGlobal] = "512";
        var (exit, outp, _) = await Run(["settings", "maintenance", "list"], store);
        exit.ShouldBe(0);
        outp.ShouldContain("embed rows per run: 512");
    }
}
