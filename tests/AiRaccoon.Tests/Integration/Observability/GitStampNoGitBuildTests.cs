using System.Reflection;
using AiRaccoon.Core.Observability;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Observability;

/// <summary>
///     R2 unavailable-stamp contract: a build with no `.git` beside it (a source tarball, a
///     restore-only build) must never fail, and the resulting assembly's build stamp must degrade
///     to commit "unknown" / timestamp null rather than staying silent about it
///     (docs/plans/2026-08-15-performance-metrics-implementation.md section 7 R2). Builds the real
///     GitStamp.targets file, copied into a temp directory with no ancestor `.git` — proving the
///     production target, not a re-implementation of it.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class GitStampNoGitBuildTests
{
    [Fact]
    public async Task Build_WithNoGitRepositoryAvailable_Succeeds_AndDegradesToTheUnavailableStampContract()
    {
        var root = TestData.CreateTempRoot("gitstamp-no-git");
        try
        {
            var runId = Guid.NewGuid().ToString("N");
            var sampleDir = Path.Combine(root, "Sample");
            Directory.CreateDirectory(sampleDir);

            var cancellationToken = TestContext.Current.CancellationToken;
            File.Copy(TestData.RepoFile("GitStamp.targets"), Path.Combine(root, "GitStamp.targets"));
            await File.WriteAllTextAsync(Path.Combine(root, "Directory.Build.props"), DirectoryBuildProps, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(sampleDir, "Sample.csproj"), SampleCsproj(runId), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(sampleDir, "Program.cs"), "System.Console.WriteLine(\"hi\");", cancellationToken);

            var csprojPath = Path.Combine(sampleDir, "Sample.csproj");
            var run = await RaccoonProcess.RunAsync(
                "dotnet",
                ["build", csprojPath, "--nologo", "-v:quiet"],
                TimeSpan.FromSeconds(120),
                TestContext.Current.CancellationToken);

            run.ExitCode.ShouldBe(0, $"a build with no git available at build time must never fail. stderr: {run.Stderr}");

            var dllPath = Path.Combine(sampleDir, "bin", "Debug", "net10.0", $"Sample_{runId}.dll");
            var assembly = Assembly.LoadFrom(dllPath);
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var commitTimestampAttribute = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .SingleOrDefault(a => a.Key == "CommitTimestamp");

            commitTimestampAttribute.ShouldBeNull("no git was available at build time; the attribute must not be emitted");

            var stamp = BuildStamp.Parse(informational, commitTimestampAttribute?.Value);
            stamp.Commit.ShouldBe(BuildStamp.UnknownCommit);
            stamp.CommitTimestamp.ShouldBeNull();
        }
        finally
        {
            TestData.DeleteTempRoot(root);
        }
    }

    private const string DirectoryBuildProps = """
        <Project>
            <PropertyGroup>
                <Version>9.9.9</Version>
            </PropertyGroup>
            <Import Project="$(MSBuildThisFileDirectory)GitStamp.targets" />
        </Project>
        """;

    private static string SampleCsproj(string runId) => $"""
        <Project Sdk="Microsoft.NET.Sdk">
            <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
                <AssemblyName>Sample_{runId}</AssemblyName>
            </PropertyGroup>
        </Project>
        """;
}
