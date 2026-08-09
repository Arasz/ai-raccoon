namespace AiRaccoon.Core.Access;

/// <summary>
///     Mode resolution (FR-NM-2; docs/work/features-native-memory/native-memory.feature): a per-project
///     setting overrides the global default (rw); modes live in the settings keys below.
/// </summary>
public static class AccessModePolicy
{
    public const string GlobalSettingKey = "access.mode.global";

    public static string ProjectSettingKey(string projectId) => $"access.mode.project:{projectId}";

    public static AccessMode Resolve(AccessMode? global, AccessMode? perProject) => perProject ?? global ?? AccessMode.Rw;

    public static bool Allows(AccessMode mode, AccessRequirement requirement) =>
        requirement switch
        {
            AccessRequirement.Read => true,
            AccessRequirement.Write => mode is AccessMode.Rw or AccessMode.Full,
            AccessRequirement.Destructive => mode == AccessMode.Full,
            _ => throw new ArgumentOutOfRangeException(nameof(requirement), requirement, null)
        };

    public static AccessMode RequiredFor(AccessRequirement requirement) =>
        requirement switch
        {
            AccessRequirement.Read => AccessMode.Ro,
            AccessRequirement.Write => AccessMode.Rw,
            AccessRequirement.Destructive => AccessMode.Full,
            _ => throw new ArgumentOutOfRangeException(nameof(requirement), requirement, null)
        };

    public static AccessMode? Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "ro" => AccessMode.Ro,
            "rw" => AccessMode.Rw,
            "full" => AccessMode.Full,
            _ => null
        };

    public static string Serialize(AccessMode mode) =>
        mode switch
        {
            AccessMode.Ro => "ro",
            AccessMode.Rw => "rw",
            AccessMode.Full => "full",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
}
