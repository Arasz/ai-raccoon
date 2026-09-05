using AiRaccoon.Core.Access;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Projects;

/// <summary>
///     ADR-0099 steady state: the key constructors fold through the empty default, i.e. they embed
///     the id verbatim (guid D-form normalized). Former loser spellings no longer build winner keys —
///     folding needs a one-shot <c>--map</c> repair, never construction.
///     <para>
///         Honesty ledger (mutation : filter : fixture): unfold-one-factory :
///         --filter ScopeProject_OfAKnownLoser_BuildsTheWinnerKey (and the three siblings) :
///         loser/canonical/global InlineData legs.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(ProjectIdAliasDefaultCollection.Name)]
public sealed class ProjectKeyFoldTests
{
    [Theory]
    [InlineData("job-search-ai-assistant", "ingest.scope.job-search-ai-assistant")]
    [InlineData("AI-RACCOON", "ingest.scope.AI-RACCOON")]
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
    [InlineData("job-search-ai-assistant", "watch.enabled.job-search-ai-assistant")]
    [InlineData("AI-RACCOON", "watch.enabled.AI-RACCOON")]
    [InlineData("jsaa", "watch.enabled.jsaa")]
    public void EnabledProject_OfAKnownLoser_BuildsTheWinnerKey(string projectId, string expected)
    {
        // Ledger — unfold-EnabledProject : --filter EnabledProject_OfAKnownLoser_BuildsTheWinnerKey : loser/canonical InlineData legs.
        WatchConfigKeys.EnabledProject(projectId).ShouldBe(expected);
    }

    [Theory]
    [InlineData("job-search-ai-assistant", "watch.concurrency.job-search-ai-assistant")]
    [InlineData("AI-RACCOON", "watch.concurrency.AI-RACCOON")]
    [InlineData("jsaa", "watch.concurrency.jsaa")]
    public void ConcurrencyProject_OfAKnownLoser_BuildsTheWinnerKey(string projectId, string expected)
    {
        // Ledger — unfold-ConcurrencyProject : --filter ConcurrencyProject_OfAKnownLoser_BuildsTheWinnerKey : loser/canonical InlineData legs.
        WatchConfigKeys.ConcurrencyProject(projectId).ShouldBe(expected);
    }

    [Theory]
    [InlineData("job-search-ai-assistant", "access.mode.project:job-search-ai-assistant")]
    [InlineData("AI-RACCOON", "access.mode.project:AI-RACCOON")]
    [InlineData("jsaa", "access.mode.project:jsaa")]
    public void ProjectSettingKey_OfAKnownLoser_BuildsTheWinnerKey(string projectId, string expected)
    {
        // Ledger — unfold-ProjectSettingKey : --filter ProjectSettingKey_OfAKnownLoser_BuildsTheWinnerKey : loser/canonical InlineData legs.
        AccessModePolicy.ProjectSettingKey(projectId).ShouldBe(expected);
    }
}
