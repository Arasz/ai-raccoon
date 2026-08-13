using System.Text.RegularExpressions;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Maintenance-config commands pin the settings-key contract the bank-maintenance hosted
///     service reads (checkpoint interval, vacuum interval); the list verb also reports live
///     bank disk stats with delta-vs-previous via the maintenance-stats.json sidecar.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
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

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private async Task<(int Exit, string Out, string Err)> Run(string[] args, FakeConfigStore store)
    {
        CliArgs.TryParse(args, out var parsed);
        parsed!.Errors.ShouldBeEmpty();
        parsed!.CommandPath.ShouldNotBeEmpty();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await TestData.CreateConfigCommands(store, maintenance: new MaintenanceCommands(_factory))
            .RunAsync(parsed!, new StandardStreams(TextReader.Null, stdout, stderr), TestContext.Current.CancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

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
    public async Task MaintenanceVacuumIntervalOutOfRange_Returns1_AndWritesError()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["maintenance", "vacuum-interval", "20000000"], store);

        exit.ShouldBe(1);
        err.ShouldContain("days");
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
    public async Task MaintenanceList_ShowsDiskStats_AndReclaimable()
    {
        var store = new FakeConfigStore();
        await SeedFreelistAsync();

        var (exit, outp, _) = await Run(["maintenance", "list"], store);

        exit.ShouldBe(0);
        outp.ShouldContain("db file:");
        outp.ShouldContain("on-disk total:");
        outp.ShouldContain("reclaimable:");
        outp.ShouldContain("freelist");
        // Decimal separator must be invariant (a locale with comma decimals would break parsing).
        Regex.IsMatch(outp, @"\d+\.\d+ MB").ShouldBeTrue();
    }

    [Fact]
    public async Task MaintenanceList_SecondCall_ShowsDelta()
    {
        var store = new FakeConfigStore();
        var (exit1, outp1, _) = await Run(["maintenance", "list"], store);
        exit1.ShouldBe(0);
        outp1.ShouldContain("no previous measurement");

        await WriteRowAsync("probe");

        var (exit2, outp2, _) = await Run(["maintenance", "list"], store);
        exit2.ShouldBe(0);
        outp2.ShouldContain("since last check:");
    }

    [Fact]
    public async Task MaintenanceList_WritesStatsSidecar()
    {
        var store = new FakeConfigStore();

        await Run(["maintenance", "list"], store);

        File.Exists(Path.Combine(_dataRoot, "maintenance-stats.json")).ShouldBeTrue();
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
