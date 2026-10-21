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
    public void Generate_ReportsNothingForAToolThatDeclaresNoScope()
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

        // The error belongs to the analyzer, so the build reports it once: the generator emits the
        // classification and says nothing about a tool it cannot classify (ADR-0139).
        result.Diagnostics.ShouldBeEmpty();
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
