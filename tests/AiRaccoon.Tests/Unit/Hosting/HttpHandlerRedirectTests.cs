using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Hosting;

/// <summary>
///     Every hand-built <c>SocketsHttpHandler</c> must refuse redirects. .NET strips
///     <c>Authorization</c> across a host hop but not a custom header, so a redirect on a client
///     carrying <c>X-AiRaccoon-Token</c> hands the loopback token to whoever answered (ADR-0022).
///     Derived from the source rather than pinned, so a third handler cannot be added unguarded.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class HttpHandlerRedirectTests
{
    [Fact]
    public void EverySocketsHttpHandler_RefusesRedirects()
    {
        var unguarded = SourceFiles()
            .SelectMany(file => Regex
                .Matches(File.ReadAllText(file), @"new SocketsHttpHandler(?<init>\s*\{[^}]*\})?")
                .Where(m => !m.Groups["init"].Value.Contains("AllowAutoRedirect = false", StringComparison.Ordinal))
                .Select(_ => Path.GetFileName(file)))
            .Order(StringComparer.Ordinal)
            .ToList();

        unguarded.ShouldBeEmpty(
            "these files construct a SocketsHttpHandler without AllowAutoRedirect = false, so a redirect "
            + "could carry the loopback token off-machine: " + string.Join(", ", unguarded));
    }

    [Fact]
    public void TheGuardSeesTheHandlers()
    {
        // Without this the guard passes vacuously the day the source moves or the type is renamed.
        SourceFiles()
            .Sum(file => Regex.Matches(File.ReadAllText(file), "new SocketsHttpHandler").Count)
            .ShouldBeGreaterThanOrEqualTo(2);
    }

    private static List<string> SourceFiles() =>
    [
        // RepoFile locates files, not directories, so anchor on the solution and step across.
        .. Directory.EnumerateFiles(
                Path.Combine(Path.GetDirectoryName(TestData.RepoFile("AiRaccoon.slnx"))!, "src"),
                "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
    ];
}
