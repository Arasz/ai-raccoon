using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Projects;

/// <summary>Turns a name lookup's ids into the one id it names, or refuses.</summary>
public static class ProjectNameMatch
{
    /// <summary>The only id in <paramref name="ids" />; throws when there is none or more than one.</summary>
    public static string Single(string name, IReadOnlyList<string> ids)
    {
        Guard.IsNotNull(name);
        Guard.IsNotNull(ids);
        return ids.Count switch
        {
            0 => throw new ProjectNotFoundException(name),
            1 => ids[0],
            _ => throw new ProjectNameAmbiguousException(name, ids)
        };
    }
}
