using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AiRaccoon.Generators;

/// <summary>
///     Reports an MCP tool that declares no scope. A tool that states no scope would be allowed
///     on the reserved shared target by default, which is the drift the declaration closes
///     (ADR-0139): the tool's own declaration is the decision, and the compiler and this
///     analyzer hold the line.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ToolScopeAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [ToolScopeFacts.MissingScope];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start => start.RegisterSymbolAction(
            AnalyzeType,
            SymbolKind.NamedType));
    }

    private static void AnalyzeType(SymbolAnalysisContext context)
    {
        if (context.Symbol is not INamedTypeSymbol type)
        {
            return;
        }

        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (!ToolScopeFacts.IsToolMethod(method) ||
                ToolScopeFacts.DeclaredScope(method) is not null)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                ToolScopeFacts.MissingScope,
                method.Locations.FirstOrDefault(),
                method.Name));
        }
    }
}
