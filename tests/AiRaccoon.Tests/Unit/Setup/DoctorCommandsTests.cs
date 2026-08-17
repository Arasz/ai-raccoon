using System.Security.Cryptography;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     GH #357: `ai-raccoon doctor` verifies the bank's schema shape and reports — it never
///     repairs. Driven end-to-end through <see cref="CliRun" /> with real argv, against a real
///     temp-dir bank, matching <c>NoiseEntriesCommandsTests</c>' style.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DoctorCommandsTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("doctor-cli");
    private readonly InfrastructureOptions _options;
    private readonly SqliteConnectionFactory _factory;

    public DoctorCommandsTests()
    {
        _options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(_options, NullKeyProvider.Resolver(_options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private Task<(int Exit, string Out, string Err)> Run(DoctorCommands doctor, string[] args) =>
        CliRun.RunAsync(args, TestData.CreateConfigCommands(new FakeConfigStore(), doctor: doctor));

    private DoctorCommands CreateDoctor(IEncryptionKeyResolver? resolver = null) =>
        new(_factory, resolver ?? NullKeyProvider.Resolver(_options), NullLogger<DoctorCommands>.Instance);

    [Fact]
    public async Task Doctor_NoBankFile_ReportsNothingToCheckAndExitsZero()
    {
        var (exit, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        exit.ShouldBe(0);
        outp.ShouldContain("no bank");
    }

    [Fact]
    public async Task Doctor_HealthyBank_ReportsHealthyAndExitsZero()
    {
        await using (await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
        }

        var (exit, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        exit.ShouldBe(0);
        outp.ShouldContain("HEALTHY");
    }

    /// <summary>The exact GH #357 repro: an `entries` table already exists with a narrower shape before doctor ever touches it.</summary>
    [Fact]
    public async Task Doctor_HandSurgeredEntriesTable_DetectsShapeMismatchAndFailsDistinctExitCode()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_factory.BankPath)!);
        await using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _factory.BankPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
        {
            await raw.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = raw.CreateCommand();
            create.CommandText = "CREATE TABLE entries (only_one_column TEXT)";
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var beforeHash = FileSha256(_factory.BankPath);

        var (exit, outp, err) = await Run(CreateDoctor(), ["doctor"]);

        // The narrow table must survive the check byte-for-byte: a mutating open (OpenBankAsync)
        // would try to migrate/"heal" this exact bank instead of only reporting on it. Asserted
        // before the exit-code/content checks below so a mutation is never masked by an earlier failure.
        FileSha256(_factory.BankPath).ShouldBe(beforeHash, "doctor must never write to a bank it is diagnosing, healthy or not");

        exit.ShouldBe(ExitCode.SchemaVerificationFailed);
        exit.ShouldNotBe(ExitCode.FailedToResolveEncryptionKey);
        exit.ShouldNotBe(ExitCode.FailedToOpenEncryptedBank);
        var combined = outp + err;
        combined.ShouldContain("entries");
        combined.ShouldContain("missing column");
        combined.ShouldContain("never repair", Case.Insensitive);
    }

    [Fact]
    public async Task Doctor_NeverModifiesTheBank()
    {
        await using (await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
        }

        var beforeHash = FileSha256(_factory.BankPath);
        var (beforeVersion, beforeAppId) = await ReadHeaderAsync();

        var (exit, _, _) = await Run(CreateDoctor(), ["doctor"]);

        exit.ShouldBe(0);
        var afterHash = FileSha256(_factory.BankPath);
        var (afterVersion, afterAppId) = await ReadHeaderAsync();

        afterHash.ShouldBe(beforeHash, "doctor must never write to the bank it is inspecting");
        afterVersion.ShouldBe(beforeVersion);
        afterAppId.ShouldBe(beforeAppId);
    }

    [Fact]
    public async Task Doctor_WhenTheEncryptionKeyCannotBeResolved_ReportsADistinctExitCode()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_factory.BankPath)!);
        await using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _factory.BankPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
        {
            await raw.OpenAsync(TestContext.Current.CancellationToken);
        }

        var (exit, _, err) = await Run(CreateDoctor(new ThrowingKeyResolver()), ["doctor"]);

        exit.ShouldBe(ExitCode.FailedToResolveEncryptionKey);
        exit.ShouldNotBe(ExitCode.SchemaVerificationFailed);
        err.ShouldContain("encryption key");
    }

    private async Task<(long Version, int AppId)> ReadHeaderAsync()
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _factory.BankPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "PRAGMA user_version";
        var version = (long)(await versionCmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        await using var appIdCmd = connection.CreateCommand();
        appIdCmd.CommandText = "PRAGMA application_id";
        var appId = (int)(long)(await appIdCmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        return (version, appId);
    }

    private static string FileSha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class ThrowingKeyResolver : IEncryptionKeyResolver
    {
        public Task<ResolvedKey> ResolveAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated encryption key resolution failure");
    }
}
