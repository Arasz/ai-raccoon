using AiRaccoon.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Shouldly;
using Xunit;

namespace AiRaccoon.Generators.Tests;

/// <summary>
///     The tool scope is declared on the tool, and the generator compiles the declaration into the
///     classification. A tool method that declares no scope is a build error, not a silent
///     default — that is what stops the classification from drifting from the tool surface.
/// </summary>
public sealed class McpToolScopeGeneratorTests
{
    [Fact]
    public void Generate_ReportsDiagnosticWhenAToolDeclaresNoScope()
    {
        const string source = """
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

        var result = Run(source);

        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Id == "AIRGEN002");
        var diagnostic = result.Diagnostics.Single(diagnostic => diagnostic.Id == "AIRGEN002");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.GetMessage().ShouldContain("Scopeless");
    }

    [Fact]
    public void Generate_EmitsTheScopeAsDataOnly()
    {
        const string source = """
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
                [System.AttributeUsage(System.AttributeTargets.Method)]
                public sealed class McpToolScopeAttribute : System.Attribute
                {
                    public McpToolScopeAttribute(bool requiresOrdinaryProject) { }
                }

                public sealed class ToolGroup
                {
                    [ModelContextProtocol.Server.McpServerTool(Name = "scoped_tool")]
                    [McpToolScope(true)]
                    public void Scoped() { }
                }
            }
            """;

        var result = Run(source);

        result.Diagnostics.ShouldBeEmpty();
        var generated = result.GeneratedTrees
            .Single(tree => tree.FilePath.EndsWith("McpToolScope.g.cs", StringComparison.Ordinal))
            .GetText(TestContext.Current.CancellationToken).ToString();
        generated.ShouldContain("McpToolScopeGenerated");
        generated.ShouldContain("\"scoped_tool\", true");
        // Data only: the interface it feeds stays a hand-written adapter (ADR-0139).
        generated.ShouldNotContain("IToolNameScope");
    }

    private static GeneratorDriverRunResult Run(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(SourceText.From(source));
        var trustedAssemblies = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var references = trustedAssemblies.Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "GeneratorTests",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new McpToolInventoryGenerator().AsSourceGenerator());

        return driver.RunGenerators(compilation).GetRunResult();
    }
}
