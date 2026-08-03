using System.Runtime.CompilerServices;
using FluentValidation;

namespace AiRaccoon.Core.Validation;

/// <summary>
///     Configures FluentValidation once per process: validation error property paths use camelCase
///     (projectId, minScore) so they match the JSON tool-argument names (csharp.instructions.md).
/// </summary>
internal static class ValidatorConfiguration
{
    [ModuleInitializer]
    internal static void ConfigureCamelCasePropertyPaths() =>
        ValidatorOptions.Global.PropertyNameResolver = (_, member, _) =>
            member.Name.Length == 0 ? member.Name : char.ToLowerInvariant(member.Name[0]) + member.Name[1..];
}
