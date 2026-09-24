using System.Security.Cryptography;
using System.Text;
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

    /// <summary>
    ///     Blocking security finding on #729: a leaked sidecar must not let an attacker brute-force
    ///     a human-typed passphrase at bare HKDF/HMAC speed. The tag for a passphrase-shaped key can
    ///     only be reproduced by also paying the stored PBKDF2 work factor — a naive recomputation
    ///     that skips it (the pre-fix shape: a single <see cref="HKDF.DeriveKey" /> pass straight
    ///     over the raw passphrase bytes) must NOT reproduce the stored tag.
    /// </summary>
    [Fact]
    public void MintIfMissing_PassphraseKey_TagIsNotReproducibleWithoutTheWorkFactor()
    {
        const string passphrase = "a-human-typed-passphrase";
        var sidecar = new KeyCheckSidecar(BankPath);
        sidecar.MintIfMissing(passphrase);
        var record = sidecar.Read()!;

        // Mirrors KeyCheckSidecar's pre-fix ComputeTag: HKDF straight over the raw key bytes, no
        // PBKDF2 pre-stretch and no per-bank salt.
        var unstretchedTag = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(passphrase), 32,
            info: [.. Encoding.UTF8.GetBytes("ai-raccoon-keycheck/v1"), .. record.BankId]);

        CryptographicOperations.FixedTimeEquals(unstretchedTag, record.Tag).ShouldBeFalse(
            "the stored tag must depend on the PBKDF2 work factor, not just HKDF over the raw passphrase");
        KeyCheckSidecar.Verifies(record, passphrase).ShouldBeTrue();
    }

    /// <summary>The stored work factor must meet SQLCipher's own PBKDF2 default (256,000 iterations) for a human-typed passphrase.</summary>
    [Fact]
    public void MintIfMissing_PassphraseKey_StoresAWorkFactorAtLeastSqlCipherDefault()
    {
        var sidecar = new KeyCheckSidecar(BankPath);
        sidecar.MintIfMissing("a-human-typed-passphrase");

        sidecar.Read()!.Iterations.ShouldBeGreaterThanOrEqualTo(256_000);
    }

    /// <summary>A raw SQLCipher key literal is already 32 high-entropy bytes — nothing to dictionary-attack, so the tag skips the stretch and stays cheap.</summary>
    [Fact]
    public void MintIfMissing_RawKeyLiteral_SkipsTheWorkFactor()
    {
        var sidecar = new KeyCheckSidecar(BankPath);
        sidecar.MintIfMissing("x'72d23870a80905c7043e610ec6609b352a85b07f14dbe4358e9b5ffcb50a3485'");

        sidecar.Read()!.Iterations.ShouldBe(0);
    }

    /// <summary>
    ///     Per-open cost sanity (ADR-0111): a verify against a passphrase-shaped key pays the PBKDF2
    ///     work factor on every bank open (<c>SqliteConnectionFactory.EnsureKeyCheck</c> runs it on
    ///     every open, not once per process), so the cost must stay well under what a human notices
    ///     per memory operation. A very generous ceiling — this guards against a future accidental
    ///     order-of-magnitude iteration bump, not a tight perf budget.
    /// </summary>
    [Fact]
    public void Verifies_PassphraseKey_StaysUnderAGenerousPerOpenCeiling()
    {
        var sidecar = new KeyCheckSidecar(BankPath);
        sidecar.MintIfMissing("a-human-typed-passphrase");
        var record = sidecar.Read()!;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        KeyCheckSidecar.Verifies(record, "a-human-typed-passphrase").ShouldBeTrue();
        stopwatch.Stop();

        stopwatch.ElapsedMilliseconds.ShouldBeLessThan(500,
            $"a single verify at the stored work factor took {stopwatch.ElapsedMilliseconds}ms — measure and record the real per-open cost in ADR-0111");
    }

    [Fact]
    public void Rewrite_PassphraseKey_UsesAFreshRandomSaltEachTime()
    {
        var sidecar = new KeyCheckSidecar(BankPath);
        sidecar.MintIfMissing("old-passphrase");
        var before = sidecar.Read()!;

        sidecar.Rewrite("new-passphrase");
        var after = sidecar.Read()!;

        after.Salt.ShouldNotBe(before.Salt);
    }
}
