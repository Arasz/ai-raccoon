namespace AiRaccoon.Core.Memory;

/// <summary>Search scope over the bank's contexts; all is the default (see docs/work/features-agent-memory/spec-issue-1.md §4.1).</summary>
public enum SearchScope
{
    All,
    Project,
    Shared
}
