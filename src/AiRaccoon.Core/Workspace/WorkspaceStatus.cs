namespace AiRaccoon.Core.Workspace;

/// <summary>Workspace lifecycle states; the terminal one names the trigger that ended it.</summary>
public enum WorkspaceStatus
{
    Active,
    Closed,
    Discarded
}
