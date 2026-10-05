namespace AiRaccoon.Core.Projects;

/// <summary>
///     The one registration-name rule the /projects endpoint and the CLI register verb share: at
///     most 200 characters and no control characters.
/// </summary>
public static class ProjectName
{
    /// <summary>The longest accepted name.</summary>
    public const int MaxLength = 200;

    /// <summary>The reason a name cannot be stored, or null when it is usable; a null name is allowed.</summary>
    public static string? TryGetRefusal(string? name)
    {
        if (name is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return "must not be blank";
        }

        if (name.Length > MaxLength)
        {
            return $"must be at most {MaxLength} characters";
        }

        return name.Any(char.IsControl) ? "must not contain control characters" : null;
    }
}
