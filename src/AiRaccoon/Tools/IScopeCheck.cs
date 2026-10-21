namespace AiRaccoon.Tools;

/// <summary>Checks whether a tool can use the canonical project id.</summary>
public interface IScopeCheck
{
    /// <summary>Returns the scope decision after project access and registration have passed.</summary>
    ScopeCheckResult Check(string canonicalProjectId, string toolName);
}
