using AiRaccoon.Core.Access;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Projects;

/// <summary>
///     Air-merge P4 boundary fold: the CLI key constructors fold at construction (defense in depth —
///     the authoritative fold is the ToolGate choke, review M4c/S1). A loser id must never again be
///     embedded raw in a settings key or the resolver would see two projects where the repair left one.
///     <para>
///         Honesty ledger (mutation : filter : fixture): unfold-one-factory :
///         --filter ScopeProject_OfAKnownLoser_BuildsTheWinnerKey (and the three siblings) :
///         loser/canonical/global InlineData legs.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectKeyFoldTests
{
    [Theory]
    [InlineData("job-search-ai-assistant", "ingest.scope.jsaa")]
    [InlineData("AI-RACCOON", "ingest.scope.ai-raccoon")]
    [InlineData("jsaa", "ingest.scope.jsaa")]
    [InlineData("jsaaa", "ingest.scope.jsaaa")]
    public void ScopeProject_OfAKnownLoser_BuildsTheWinnerKey(string projectId, string expected)
    {
        // Ledger — unfold-ScopeProject : --filter ScopeProject_OfAKnownLoser_BuildsTheWinnerKey : loser/canonical/typo InlineData legs.
        IngestScopeKeys.ScopeProject(projectId).ShouldBe(expected);
    }

    [Fact]
    public void ScopeProject_OfAnEmptyId_StillDerivesThePrefix()
    {
        // The resolver derives its enumeration prefix from ScopeProject(string.Empty).
        // Ledger — throw-on-blank : --filter ScopeProject_OfAnEmptyId_StillDerivesThePrefix : empty string (resolver prefix derivation).
        IngestScopeKeys.ScopeProject(string.Empty).ShouldBe("ingest.scope.");
    }

    [Theory]
    [InlineData("job-search-ai-assistant", "watch.enabled.jsaa")]
    [InlineData("AI-RACCOON", "watch.enabled.ai-raccoon")]
    [InlineData("jsaa", "watch.enabled.jsaa")]
    public void EnabledProject_OfAKnownLoser_BuildsTheWinnerKey(string projectId, string expected)
    {
        // Ledger — unfold-EnabledProject : --filter EnabledProject_OfAKnownLoser_BuildsTheWinnerKey : loser/canonical InlineData legs.
        WatchConfigKeys.EnabledProject(projectId).ShouldBe(expected);
    }

    [Theory]
    [InlineData("job-search-ai-assistant", "watch.concurrency.jsaa")]
    [InlineData("AI-RACCOON", "watch.concurrency.ai-raccoon")]
    [InlineData("jsaa", "watch.concurrency.jsaa")]
    public void ConcurrencyProject_OfAKnownLoser_BuildsTheWinnerKey(string projectId, string expected)
    {
        // Ledger — unfold-ConcurrencyProject : --filter ConcurrencyProject_OfAKnownLoser_BuildsTheWinnerKey : loser/canonical InlineData legs.
        WatchConfigKeys.ConcurrencyProject(projectId).ShouldBe(expected);
    }

    [Theory]
    [InlineData("job-search-ai-assistant", "access.mode.project:jsaa")]
    [InlineData("AI-RACCOON", "access.mode.project:ai-raccoon")]
    [InlineData("jsaa", "access.mode.project:jsaa")]
    public void ProjectSettingKey_OfAKnownLoser_BuildsTheWinnerKey(string projectId, string expected)
    {
        // Ledger — unfold-ProjectSettingKey : --filter ProjectSettingKey_OfAKnownLoser_BuildsTheWinnerKey : loser/canonical InlineData legs.
        AccessModePolicy.ProjectSettingKey(projectId).ShouldBe(expected);
    }
}
