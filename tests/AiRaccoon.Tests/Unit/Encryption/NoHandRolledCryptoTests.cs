using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Encryption;

/// <summary>
///     Enforces the "no hand-rolled crypto or security orchestration" invariant: key derivation goes
///     through a platform KDF, and raw hash primitives stay on the documented content-addressing sites.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class NoHandRolledCryptoTests
{
    /// <summary>Raw primitives are fine for content addressing and integrity, never for deriving a key.</summary>
    private static readonly HashSet<string> ContentAddressingSites = new(StringComparer.Ordinal)
    {
        "AiRaccoon.Core/Memory/ContentHash.cs",
        "AiRaccoon.Infrastructure/Embedding/BundledModel.cs",
        "AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs",
        "AiRaccoon.Infrastructure/Sqlite/SnippetFallback.cs",
        "AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs",
        "AiRaccoon.Infrastructure/Watch/WatchDigestExecutor.cs"
    };

    /// <summary>The one file allowed to build a bank key; its legacy path exists only to rekey old banks (ADR-0012).</summary>
    private const string KeyDerivationSite = "AiRaccoon.Core/Encryption/SshKeyDerivation.cs";

    private static readonly Regex RawPrimitive =
        new(@"\b(MD5|SHA1|SHA256|SHA384|SHA512|HMAC(MD5|SHA\d+)?|TripleDES|DES|RC2)\s*\.", RegexOptions.Compiled);

    private static readonly Regex BannedKdf =
        new(@"\b(Rfc2898DeriveBytes|PasswordDeriveBytes)\b", RegexOptions.Compiled);

    [Fact]
    public void RawHashPrimitives_AppearOnlyOnDocumentedSites()
    {
        var offenders = ProductionSources()
            .Where(f => RawPrimitive.IsMatch(StripComments(File.ReadAllText(f.FullPath))))
            .Select(f => f.Relative)
            .Where(r => r != KeyDerivationSite && !ContentAddressingSites.Contains(r))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "Raw hash primitives are for content addressing, not key material. If this is a content hash, "
            + "add the file to ContentAddressingSites; if it derives a key, use System.Security.Cryptography.HKDF.");
    }

    /// <summary>
    ///     Scoped to DeriveRawKey's own body, not the whole file, so a reverted DeriveRawKey can't hide
    ///     behind DeriveLegacyRawKey's still-correct HKDF.DeriveKey call elsewhere in the file.
    /// </summary>
    [Fact]
    public void BankKeyDerivation_RawKeyMethodSpecifically_UsesThePlatformKdf()
    {
        var derivation = ProductionSources().Single(f => f.Relative == KeyDerivationSite);
        var source = File.ReadAllText(derivation.FullPath);

        var body = ExtractMethodBody(source, "DeriveRawKey");

        body.ShouldContain("HKDF.DeriveKey",
            customMessage: "DeriveRawKey — the derivation new banks actually use — must call the platform KDF "
            + "(ADR-0012), not a hand-assembled hash. An HKDF.DeriveKey call elsewhere in the file (e.g. only in "
            + "DeriveLegacyRawKey) does not satisfy this.");
    }

    /// <summary>The method's own brace-balanced `{ … }` block, comments stripped — never the next method's leading doc comment.</summary>
    private static string ExtractMethodBody(string source, string methodName)
    {
        var signatureIndex = source.IndexOf($"string {methodName}(", StringComparison.Ordinal);
        signatureIndex.ShouldBeGreaterThanOrEqualTo(0, $"{methodName} not found — has it been renamed or moved?");

        var braceStart = source.IndexOf('{', signatureIndex);
        braceStart.ShouldBeGreaterThanOrEqualTo(0, $"{methodName}'s opening brace not found");

        var depth = 0;
        for (var i = braceStart; i < source.Length; i++)
        {
            switch (source[i])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return StripComments(source[braceStart..(i + 1)]);
                    }

                    break;
            }
        }

        throw new InvalidOperationException($"{methodName}'s closing brace not found — unbalanced braces?");
    }

    [Fact]
    public void ObsoletePasswordKdfs_AreNotUsed()
    {
        var offenders = ProductionSources()
            .Where(f => BannedKdf.IsMatch(StripComments(File.ReadAllText(f.FullPath))))
            .Select(f => f.Relative)
            .ToList();

        offenders.ShouldBeEmpty("Use System.Security.Cryptography.HKDF for key derivation.");
    }

    private static IEnumerable<(string FullPath, string Relative)> ProductionSources()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(p => (p, Path.GetRelativePath(src, p).Replace(Path.DirectorySeparatorChar, '/')));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AiRaccoon.slnx")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("could not locate the repository root from the test output directory");
        return dir.FullName;
    }

    /// <summary>Line and block comments only — a mention in prose is not a use.</summary>
    private static string StripComments(string source) =>
        Regex.Replace(Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");
}
