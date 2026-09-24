using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Encryption;

/// <summary>
///     ADR-0111: memory.db.keycheck tells a wrong key apart from a corrupt bank when SQLCipher
///     answers SQLITE_NOTADB for both. It holds a random bank-id and a tag proving which key
///     produced it — never the key itself — and refuses (never chmods) a file another principal
///     could read or write, the same stance IdentityKeyFile/McpTokenFile take.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class KeyCheckSidecarTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("keycheck-sidecar");

    private string BankPath => Path.Combine(_dataRoot, "memory.db");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public void Read_NoSidecar_ReturnsNull() =>
        new KeyCheckSidecar(BankPath).Read().ShouldBeNull();

    [Fact]
    public void PathFor_IsTheBankPathWithTheKeycheckSuffix() =>
        KeyCheckSidecar.PathFor(BankPath).ShouldBe($"{BankPath}.keycheck");

    [Fact]
    public void MintIfMissing_ThenRead_VerifiesTheSameKey()
    {
        var sidecar = new KeyCheckSidecar(BankPath);

        sidecar.MintIfMissing("bank-key-A");

        var record = sidecar.Read();
        record.ShouldNotBeNull();
        KeyCheckSidecar.Verifies(record, "bank-key-A").ShouldBeTrue();
        KeyCheckSidecar.Verifies(record, "bank-key-B").ShouldBeFalse();
    }

    [Fact]
    public void MintIfMissing_FileAlreadyExists_DoesNotOverwriteIt()
    {
        var sidecar = new KeyCheckSidecar(BankPath);
        sidecar.MintIfMissing("bank-key-A");
        var before = File.ReadAllBytes(KeyCheckSidecar.PathFor(BankPath));

        sidecar.MintIfMissing("bank-key-B");

        File.ReadAllBytes(KeyCheckSidecar.PathFor(BankPath)).ShouldBe(before);
        KeyCheckSidecar.Verifies(sidecar.Read()!, "bank-key-A").ShouldBeTrue();
    }

    [Fact]
    public void Mint_WritesAnOwnerOnlyFile()
    {
        var sidecar = new KeyCheckSidecar(BankPath);
        sidecar.MintIfMissing("bank-key-A");

        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(KeyCheckSidecar.PathFor(BankPath))
                .ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void Rewrite_ChangesTheStoredTagToTheNewKey()
    {
        var sidecar = new KeyCheckSidecar(BankPath);
        sidecar.MintIfMissing("old-key");

        sidecar.Rewrite("new-key");

        var record = sidecar.Read();
        KeyCheckSidecar.Verifies(record!, "new-key").ShouldBeTrue();
        KeyCheckSidecar.Verifies(record!, "old-key").ShouldBeFalse();
    }

    [Fact]
    public void Rewrite_LeavesNoTempFileBehind()
    {
        var sidecar = new KeyCheckSidecar(BankPath);
        sidecar.MintIfMissing("old-key");

        sidecar.Rewrite("new-key");

        Directory.GetFiles(_dataRoot).ShouldBe([KeyCheckSidecar.PathFor(BankPath)]);
    }

    [Fact]
    public void Read_SidecarReadableByGroup_ThrowsWithTheChmodRemedy()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // POSIX permission bits only
        }

        var sidecar = new KeyCheckSidecar(BankPath);
        sidecar.MintIfMissing("bank-key-A");
        File.SetUnixFileMode(KeyCheckSidecar.PathFor(BankPath), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var ex = Should.Throw<KeyCheckViolation>(() => sidecar.Read());

        ex.Message.ShouldContain(KeyCheckSidecar.PathFor(BankPath));
        ex.Message.ShouldContain("chmod 600");
    }

    /// <summary>Refuse, never auto-chmod: a planted or leaked sidecar must not be silently tightened and trusted.</summary>
    [Fact]
    public void Rewrite_SidecarWritableByOther_ThrowsAndDoesNotOverwrite()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // POSIX permission bits only
        }

        var sidecar = new KeyCheckSidecar(BankPath);
        sidecar.MintIfMissing("old-key");
        var before = File.ReadAllBytes(KeyCheckSidecar.PathFor(BankPath));
        File.SetUnixFileMode(KeyCheckSidecar.PathFor(BankPath), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);

        Should.Throw<KeyCheckViolation>(() => sidecar.Rewrite("new-key"));

        File.SetUnixFileMode(KeyCheckSidecar.PathFor(BankPath), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.ReadAllBytes(KeyCheckSidecar.PathFor(BankPath)).ShouldBe(before);
    }

    [Fact]
    public void Read_MalformedContent_ThrowsKeyCheckViolation()
    {
        Directory.CreateDirectory(_dataRoot);
        File.WriteAllText(KeyCheckSidecar.PathFor(BankPath), "not a key-check record");

        Should.Throw<KeyCheckViolation>(() => new KeyCheckSidecar(BankPath).Read());
    }
}
