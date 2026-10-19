using AiRaccoon.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Shouldly;
using Xunit;

namespace AiRaccoon.Generators.Tests;

public sealed class McpToolInventoryGeneratorTests
{
    [Fact]
    public void Generate_UsesExplicitConstantNamesInOrdinalOrder()
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
                internal static class ToolNames
                {
                    public const string Zebra = "zebra_tool";
                    public const string Alpha = "alpha_tool";
                }

                public sealed class ToolGroup
                {
                    [ModelContextProtocol.Server.McpServerTool(Name = ToolNames.Zebra)]
                    public void Zebra() { }

                    [ModelContextProtocol.Server.McpServerTool(Name = ToolNames.Alpha)]
                    public void Alpha() { }
                }
            }
            """;

        var result = Run(source);

        result.Diagnostics.ShouldBeEmpty();
        var generated = result.GeneratedTrees.Single().GetText(TestContext.Current.CancellationToken).ToString();
        generated.IndexOf("alpha_tool", StringComparison.Ordinal).ShouldBeLessThan(
            generated.IndexOf("zebra_tool", StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_OnlyIncludesPublicInstanceMethodsOnConcreteToolTypesInTheToolNamespace()
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
                public sealed class IncludedTools
                {
                    [ModelContextProtocol.Server.McpServerTool(Name = "included_tool")]
                    public void Included() { }

                    [ModelContextProtocol.Server.McpServerTool(Name = "private_tool")]
                    private void Private() { }

                    [ModelContextProtocol.Server.McpServerTool(Name = "static_tool")]
                    public static void Static() { }
                }

                public abstract class AbstractTools
                {
                    [ModelContextProtocol.Server.McpServerTool(Name = "abstract_tool")]
                    public void Abstract() { }
                }
            }

            namespace OutsideTools
            {
                public sealed class OtherTools
                {
                    [ModelContextProtocol.Server.McpServerTool(Name = "outside_tool")]
                    public void Outside() { }
                }
            }
            """;

        var result = Run(source);

        result.Diagnostics.ShouldBeEmpty();
        var generated = result.GeneratedTrees.Single().GetText(TestContext.Current.CancellationToken).ToString();
        generated.ShouldContain("included_tool");
        generated.ShouldNotContain("private_tool");
        generated.ShouldNotContain("static_tool");
        generated.ShouldNotContain("abstract_tool");
        generated.ShouldNotContain("outside_tool");
    }

    [Fact]
    public void Generate_ReportsDiagnosticWhenARequiredNameIsMissing()
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
                    [ModelContextProtocol.Server.McpServerTool]
                    public void MissingName() { }
                }
            }
            """;

        var result = Run(source);

        var diagnostic = result.Diagnostics.Single(diagnostic => diagnostic.Id == "AIRGEN001");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.GetMessage().ShouldContain("MissingName");
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
