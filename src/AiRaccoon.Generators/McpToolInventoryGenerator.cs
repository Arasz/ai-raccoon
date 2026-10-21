using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AiRaccoon.Generators;

[Generator]
public sealed class McpToolInventoryGenerator : IIncrementalGenerator
{
    private const string ToolAttribute = "ModelContextProtocol.Server.McpServerToolAttribute";
    private const string ToolNamespace = "AiRaccoon.Tools";
    private const string ScopeAttribute = "AiRaccoon.Tools.McpToolScopeAttribute";

    private static readonly DiagnosticDescriptor MissingName = new(
        "AIRGEN001",
        "MCP tool name is required",
        "MCP tool method '{0}' must declare a non-empty Name",
        "McpToolInventory",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MissingScope = new(
        "AIRGEN002",
        "MCP tool scope is required",
        "MCP tool method '{0}' must declare its scope with [McpToolScope(ToolScope. …)]",
        "McpToolInventory",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var tools = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ToolAttribute,
                static (node, _) => node is MethodDeclarationSyntax,
                static (attributeContext, _) => CreateTool(attributeContext))
            .Where(static tool => tool is not null)
            .Select(static (tool, _) => tool!);

        var toolTypes = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax,
                static (syntaxContext, _) =>
                    (INamedTypeSymbol?)syntaxContext.SemanticModel.GetDeclaredSymbol(
                        (TypeDeclarationSyntax)syntaxContext.Node))
            .Where(static type => type is { TypeKind: TypeKind.Class } &&
                                  type.ContainingNamespace.ToDisplayString() == ToolNamespace)
            .Select(static (type, _) => type!)
            .Collect();

        context.RegisterSourceOutput(
            tools.Collect().Combine(toolTypes),
            static (productionContext, input) => Generate(productionContext, input.Left, input.Right));
    }

    private static ToolCandidate? CreateTool(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not IMethodSymbol method ||
            method.ContainingType.TypeKind != TypeKind.Class ||
            method.ContainingNamespace.ToDisplayString() != ToolNamespace ||
            method.DeclaredAccessibility != Accessibility.Public ||
            method.IsStatic)
        {
            return null;
        }

        var nameArgument = context.Attributes[0].NamedArguments
            .FirstOrDefault(argument => argument.Key == "Name");
        var name = nameArgument.Value.Value as string;
        return new ToolCandidate(method.Name, method.ContainingType, name, method.Locations.FirstOrDefault(),
            ScopeOf(method));
    }

    /// <summary>
    ///     The scope the method declares, read off the method symbol in the same pass that finds
    ///     the tool: null when the method declares none, and the generator reports that as an error
    ///     (AIRGEN002). A second syntax provider would have to be joined back to the candidates by
    ///     symbol; reading the attribute here keeps one collector.
    /// </summary>
    private static string? ScopeOf(IMethodSymbol method)
    {
        foreach (var attribute in method.GetAttributes())
        {
            var attributeClass = attribute.AttributeClass;
            if (attributeClass is null ||
                attributeClass.Name != "McpToolScopeAttribute" ||
                attributeClass.ContainingNamespace.ToDisplayString() != ToolNamespace)
            {
                continue;
            }

            if (attribute.ConstructorArguments.Length == 0)
            {
                return null;
            }

            // The argument's value is the enum's underlying int; the name comes from the enum's
            // own fields, so the generated file names the scope rather than a number.
            if (attribute.ConstructorArguments[0].Value is not int underlying)
            {
                return null;
            }

            var scopeType = attributeClass.Constructors.FirstOrDefault(c => c.Parameters.Length > 0)
                ?.Parameters[0].Type;
            if (scopeType is null)
            {
                return null;
            }

            foreach (var member in scopeType.GetMembers())
            {
                if (member is IFieldSymbol { HasConstantValue: true, IsStatic: true } field &&
                    field.ConstantValue is int fieldValue && fieldValue == underlying)
                {
                    return $"global::{scopeType.ToDisplayString()}.{field.Name}";
                }
            }

            return null;
        }

        return null;
    }

    private static void Generate(
        SourceProductionContext context,
        ImmutableArray<ToolCandidate> candidates,
        ImmutableArray<INamedTypeSymbol> toolTypes)
    {
        var names = new List<string>(candidates.Length);
        var scopes = new List<(string Name, string Scope)>(candidates.Length);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Name))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    MissingName,
                    candidate.Location,
                    candidate.MethodName));
                continue;
            }

            if (candidate.Scope is not { } scope)
            {
                // The scope is the tool's own decision, and it is required: a tool that states none
                // would be allowed on the reserved shared target by default, which is the drift
                // this closes (ADR-0139).
                context.ReportDiagnostic(Diagnostic.Create(
                    MissingScope,
                    candidate.Location,
                    candidate.MethodName));
                continue;
            }

            if (!scopes.Any(scope => scope.Name == candidate.Name))
            {
                scopes.Add((candidate.Name!, scope));
            }

            var useCount = toolTypes
                .Where(type => !type.IsAbstract && IsOrInheritsFrom(type, candidate.ContainingType))
                .Distinct(SymbolEqualityComparer.Default)
                .Count();
            for (var index = 0; index < useCount; index++)
            {
                names.Add(candidate.Name!);
            }
        }

        names.Sort(StringComparer.Ordinal);

        var source = new StringBuilder();
        source.AppendLine("// <auto-generated/>");
        source.AppendLine("namespace AiRaccoon.Tools;");
        source.AppendLine();
        source.AppendLine("internal static class McpToolInventoryGenerated");
        source.AppendLine("{");
        source.AppendLine("    internal static readonly global::System.Collections.Generic.IReadOnlyList<string> Names = new string[]");
        source.AppendLine("    {");
        foreach (var name in names)
        {
            source.Append("        ").Append(SymbolDisplay.FormatLiteral(name, quote: true)).AppendLine(",");
        }

        source.AppendLine("    };");
        source.AppendLine("}");

        context.AddSource("McpToolInventory.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));

        // The classification the tool scope compiles into: data only, so the interface it feeds
        // stays a hand-written adapter (ADR-0139).
        scopes.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        var scopeSource = new StringBuilder();
        scopeSource.AppendLine("// <auto-generated/>");
        scopeSource.AppendLine("namespace AiRaccoon.Tools;");
        scopeSource.AppendLine();
        scopeSource.AppendLine("internal static class McpToolScopeGenerated");
        scopeSource.AppendLine("{");
        scopeSource.AppendLine("    internal static readonly global::System.Collections.Generic.IReadOnlyDictionary<string, global::AiRaccoon.Tools.ToolScope> ScopeByTool =");
        scopeSource.AppendLine("        new global::System.Collections.Generic.Dictionary<string, global::AiRaccoon.Tools.ToolScope>(global::System.StringComparer.Ordinal)");
        scopeSource.AppendLine("        {");
        foreach (var scope in scopes)
        {
            scopeSource.Append("            { ").Append(SymbolDisplay.FormatLiteral(scope.Name, quote: true))
                .Append(", ").Append(scope.Scope).AppendLine(" },");
        }

        scopeSource.AppendLine("        };");
        scopeSource.AppendLine("}");

        context.AddSource("McpToolScope.g.cs", SourceText.From(scopeSource.ToString(), Encoding.UTF8));
    }

    private static bool IsOrInheritsFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, baseType.OriginalDefinition))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class ToolCandidate(
        string methodName,
        INamedTypeSymbol containingType,
        string? name,
        Location? location,
        string? scope)
    {
        public string MethodName { get; } = methodName;

        public INamedTypeSymbol ContainingType { get; } = containingType;

        public string? Name { get; } = name;

        public Location? Location { get; } = location;

        public string? Scope { get; } = scope;
    }
}
