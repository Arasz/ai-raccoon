using Microsoft.CodeAnalysis;

namespace AiRaccoon.Generators;

/// <summary>
///     The one place that knows what an MCP tool is and what scope it declares. The generator
///     and the analyzer both ask it, so the classification and the error cannot disagree about
///     which methods are tools (ADR-0139).
/// </summary>
internal static class ToolScopeFacts
{
    /// <summary>The namespace the MCP tool classes live in.</summary>
    internal const string ToolNamespace = "AiRaccoon.Tools";

    /// <summary>The attribute that makes a method an MCP tool.</summary>
    internal const string ToolAttribute = "ModelContextProtocol.Server.McpServerToolAttribute";

    /// <summary>The attribute that declares a tool's scope.</summary>
    internal const string ScopeAttribute = "AiRaccoon.Tools.McpToolScopeAttribute";

    /// <summary>The error a tool that declares no scope is reported with.</summary>
    internal static readonly DiagnosticDescriptor MissingScope = new(
        "AIRGEN002",
        "MCP tool scope is required",
        "MCP tool method '{0}' must declare its scope: [McpToolScope(ToolScope.All)], [McpToolScope(ToolScope.Shared)] or [McpToolScope(ToolScope.Project)]",
        "McpToolInventory",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>True when the method is an MCP tool the surface registers.</summary>
    internal static bool IsToolMethod(IMethodSymbol method) =>
        method.ContainingType.TypeKind == TypeKind.Class &&
        method.ContainingNamespace.ToDisplayString() == ToolNamespace &&
        method.DeclaredAccessibility == Accessibility.Public &&
        !method.IsStatic &&
        HasAttribute(method, ToolAttribute);

    /// <summary>True when the method carries the attribute with that full name.</summary>
    internal static bool HasAttribute(IMethodSymbol method, string fullName)
    {
        foreach (var attribute in method.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == fullName)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The scope the method declares, as the fully qualified usage the generated file can
    ///     emit ("global::AiRaccoon.Tools.ToolScope.All"), or null when it declares none. The
    ///     argument's value is the enum's underlying int; the name comes from the enum's own
    ///     fields, so the generated file names the scope rather than a number.
    /// </summary>
    internal static string? DeclaredScope(IMethodSymbol method)
    {
        foreach (var attribute in method.GetAttributes())
        {
            var attributeClass = attribute.AttributeClass;
            if (attributeClass is null || attributeClass.ToDisplayString() != ScopeAttribute)
            {
                continue;
            }

            if (attribute.ConstructorArguments.Length == 0 ||
                attribute.ConstructorArguments[0].Value is not int underlying)
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
}
