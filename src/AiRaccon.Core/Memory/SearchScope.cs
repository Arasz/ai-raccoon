namespace AiRaccon.Core.Memory;

/// <summary>Search scope over the bank's contexts; all is the default (spec §4.1).</summary>
public enum SearchScope
{
    All,
    Project,
    Shared,
}
