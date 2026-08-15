using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Retrieval;

/// <summary>
///     What the query catalog can and cannot support as an out-of-sample measurement (docs/adr/0056).
///     These assertions are the reason the held-out gate is three queries and not a family split.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class RetrievalTuningSetsTests
{
    /// <summary>
    ///     The held-out set must never empty. Adding a query id to <see cref="RetrievalTuningSets.TuningQueryIds" />
    ///     removes its whole document from the held-out side, so an unnoticed expansion of tuning
    ///     silently converts the gate into an in-sample one. Raise history: 3 pinned 2026-08-15
    ///     (A8/A9/A10). Raising this number is progress; lowering it is the gate eroding.
    /// </summary>
    [Fact]
    public void HeldOutSet_IsNotEmpty_AndIsDocumentDisjointFromTuning()
    {
        var catalog = BaselineQueryCatalog.Load();
        var heldOut = RetrievalTuningSets.HeldOut(catalog);
        var tunedDocuments = RetrievalTuningSets.TunedDocuments(catalog);

        heldOut.Count.ShouldBeGreaterThanOrEqualTo(3,
            "the held-out gate needs queries no sweep tuned over; expanding TuningQueryIds shrinks this");
        foreach (var query in heldOut)
        {
            tunedDocuments.ShouldNotContain(RetrievalTuningSets.Document(query.ExpectedSource!),
                $"{query.Id} shares a document with a tuning query and is not held out");
        }
    }

    /// <summary>
    ///     The partition covers every gradeable query exactly once: tuning, held out, or leaked
    ///     through a shared document. A query in none of the three is one nothing scores.
    /// </summary>
    [Fact]
    public void EveryGradeableQuery_LandsInExactlyOneTier()
    {
        var catalog = BaselineQueryCatalog.Load();
        var gradeable = RetrievalTuningSets.Gradeable(catalog).Select(q => q.Id).ToList();
        var tuning = gradeable.Intersect(RetrievalTuningSets.TuningQueryIds, StringComparer.Ordinal).ToList();
        var heldOut = RetrievalTuningSets.HeldOut(catalog).Select(q => q.Id).ToList();
        var leaked = RetrievalTuningSets.DocumentLeaked(catalog).Select(q => q.Id).ToList();

        tuning.Concat(heldOut).Concat(leaked).OrderBy(id => id, StringComparer.Ordinal)
            .ShouldBe(gradeable.OrderBy(id => id, StringComparer.Ordinal), "the three tiers must partition the gradeable queries");
        tuning.Intersect(heldOut).ShouldBeEmpty();
        tuning.Intersect(leaked).ShouldBeEmpty();
        heldOut.Intersect(leaked).ShouldBeEmpty();
    }

    /// <summary>
    ///     Why the plan's leave-one-family-out control is not available here: the corpus has two
    ///     generators ('docs' and 'ai-badger') and the tuning set spans both, so no family is
    ///     unseen. This assertion fails the day someone fixes that — which is the point, and the
    ///     signal to promote the gate from document-level to family-level.
    /// </summary>
    [Fact]
    public void NoFamilyIsHeldOut_WhichIsWhyTheGateIsDocumentLevel()
    {
        var catalog = BaselineQueryCatalog.Load();
        var gradeable = RetrievalTuningSets.Gradeable(catalog);
        var tunedFamilies = gradeable
            .Where(q => RetrievalTuningSets.TuningQueryIds.Contains(q.Id))
            .Select(q => RetrievalTuningSets.Family(q.ExpectedSource!))
            .ToHashSet(StringComparer.Ordinal);
        var allFamilies = gradeable
            .Select(q => RetrievalTuningSets.Family(q.ExpectedSource!))
            .ToHashSet(StringComparer.Ordinal);

        allFamilies.ShouldBe(new HashSet<string>(StringComparer.Ordinal) { "docs", "ai-badger" },
            ignoreOrder: true);
        allFamilies.Except(tunedFamilies, StringComparer.Ordinal).ShouldBeEmpty(
            "a family the sweeps never tuned on now exists — promote the held-out gate to leave-one-family-out (docs/adr/0056)");
    }
}
