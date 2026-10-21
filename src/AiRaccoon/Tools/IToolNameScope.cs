namespace AiRaccoon.Tools;

/// <summary>Classifies the tools that require an ordinary project.</summary>
public interface IToolNameScope
{
    /// <summary>True when the tool cannot use the reserved shared target.</summary>
    bool RequiresOrdinaryProject(string toolName);
}
