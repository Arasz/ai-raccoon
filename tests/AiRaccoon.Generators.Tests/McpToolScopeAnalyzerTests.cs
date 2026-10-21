using System.Collections.Immutable;
using AiRaccoon.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Shouldly;
using Xunit;

namespace AiRaccoon.Generators.Tests;

/// <summary>
///     The analyzer owns the "a tool declares no scope" error. A tool that states no scope would
///     be allowed on the reserved shared target by default, which is the drift the declaration
///     closes, so the error is a build failure at the tool's own line.
/// </summary>
public sealed class McpToolScopeAnalyzerTests
{
    [Fact]
    public async Task Analyze_ReportsAToolThatDeclaresNoScope()
    {
        var diagnostics = await AnalyzeAsync(Snippets.ToolWithoutScope);

        var diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("AIRGEN002");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.GetMessage().ShouldContain("Scopeless");
    }

    [Fact]
    public async Task Analyze_ReportsNothingForAToolThatDeclaresItsScope() =>
        (await AnalyzeAsync(Snippets.ToolWithScope)).ShouldBeEmpty();

    [Fact]
    public async Task Analyze_ReportsNothingForAMethodThatIsNotATool()
    {
        // A private or static method with the tool attribute is not a tool the surface registers,
        // so it declares no scope and nothing is reported.
        (await AnalyzeAsync(Snippets.NotATool)).ShouldBeEmpty();
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        var withAnalyzers = McpToolInventoryGeneratorTests.Compile(source)
            .WithAnalyzers([new ToolScopeAnalyzer()], new AnalyzerOptions(additionalFiles: []));

        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    private static class Snippets
    {
        public const string ToolWithoutScope = """
            namespace ModelContextProtocol.Server
            {
                [System.AttributeUsage(System.AttributeTargets.Method)]
                public sealed class McpServerToolAttribute : System.Attribute
                {
                    public string? Name { get; set; }
                }
            }

            namespace AiRaccoon.Tools
            {
                public sealed class ToolGroup
                {
                    [ModelContextProtocol.Server.McpServerTool(Name = "scopeless_tool")]
                    public void Scopeless() { }
                }
            }
            """;

        public const string ToolWithScope = """
            namespace ModelContextProtocol.Server
            {
                [System.AttributeUsage(System.AttributeTargets.Method)]
                public sealed class McpServerToolAttribute : System.Attribute
                {
                    public string? Name { get; set; }
                }
            }

            namespace AiRaccoon.Tools
            {
                public enum ToolScope
                {
                    All,
                    Shared,
                    Project
                }

                [System.AttributeUsage(System.AttributeTargets.Method)]
                public sealed class McpToolScopeAttribute : System.Attribute
                {
                    public McpToolScopeAttribute(ToolScope scope) { }
                }

                public sealed class ToolGroup
                {
                    [ModelContextProtocol.Server.McpServerTool(Name = "scoped_tool")]
                    [McpToolScope(ToolScope.All)]
                    public void Scoped() { }
                }
            }
            """;

        public const string NotATool = """
            namespace ModelContextProtocol.Server
            {
                [System.AttributeUsage(System.AttributeTargets.Method)]
                public sealed class McpServerToolAttribute : System.Attribute
                {
                    public string? Name { get; set; }
                }
            }

            namespace AiRaccoon.Tools
            {
                public sealed class ToolGroup
                {
                    [ModelContextProtocol.Server.McpServerTool(Name = "private_tool")]
                    private void Private() { }

                    [ModelContextProtocol.Server.McpServerTool(Name = "static_tool")]
                    public static void Static() { }
                }
            }
            """;
    }
}
