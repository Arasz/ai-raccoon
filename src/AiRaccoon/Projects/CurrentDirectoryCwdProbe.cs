namespace AiRaccoon.Projects;

/// <summary>
///     The production probe: the process's actual working directory — where the host spawned
///     the stdio server, and therefore the project directory in the single-project-per-process
///     shape every supported host uses.
/// </summary>
public sealed class CurrentDirectoryCwdProbe : ICwdProbe
{
    public static readonly CurrentDirectoryCwdProbe Instance = new();

    public string CurrentDirectory => Environment.CurrentDirectory;
}
