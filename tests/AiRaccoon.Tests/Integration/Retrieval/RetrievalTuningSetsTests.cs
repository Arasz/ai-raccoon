using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Retrieval;

/// <summary>
///     What the query catalog can and cannot support as an out-of-sample measurement (docs/adr/0056,
///     docs/adr/0090). On the public docs corpus nothing has ever been tuned, so the held-out set is
///     the WHOLE gradeable catalog and these assertions exist to keep it that way.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class RetrievalTuningSetsTests
{
    /// <summary>
    ///     On this corpus the held-out set is not merely non-empty, it is everything: no parameter
    ///     sweep has ever selected against these documents. Adding a query id to
    ///     <see cref="RetrievalTuningSets.TuningQueryIds" /> removes its whole document from the
    ///     held-out side, so an unnoticed expansion of tuning silently converts the gate into an
    ///     in-sample one — ADR-0056's original defect. History: 3 held out on the jsaa corpus
    ///     (A8/A9/A10); 19 — all gradeable queries — on the public corpus (ADR-0090).
    /// </summary>
    [Fact]
    public void HeldOutSet_IsNotEmpty_AndIsDocumentDisjointFromTuning()
    {
        var catalog = BaselineQueryCatalog.Load();
        var heldOut = RetrievalTuningSets.HeldOut(catalog);
        var tunedDocuments = RetrievalTuningSets.TunedDocuments(catalog);

        heldOut.Count.ShouldBe(RetrievalTuningSets.Gradeable(catalog).Count,
            "every gradeable query must be held out: nothing has been tuned on this corpus, and a " +
            "shortfall means TuningQueryIds grew without ADR-0090 being amended");
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
    ///     The corpus carries two generators ('docs' and 'ai-badger'), and on the public corpus
    ///     NEITHER has been tuned on — the inverse of the jsaa situation, where the tuning set
    ///     spanned both and no family was unseen. A selection that collapsed to one family would
    ///     also fail here, which is what keeps corpus_config's two-family shape load-bearing.
    /// </summary>
    [Fact]
    public void EveryFamilyIsHeldOut_BecauseNothingWasTunedOnThisCorpus()
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

        tunedFamilies.ShouldBeEmpty(
            "a family has been tuned on — the held-out gate is no longer leave-everything-out and " +
            "ADR-0090's out-of-sample claim needs amending (docs/adr/0056)");
    }

    /// <summary>
    ///     The anti-drift guard ADR-0090 rests on. ADR-0056's finding was that every published
    ///     retrieval number was in-sample because the same queries selected the parameters and
    ///     gated them. That circularity is gone here only because nothing has been tuned on this
    ///     corpus. The day someone runs a sweep and records its winners in TuningQueryIds, this
    ///     goes red — which is the moment ADR-0090's "every gradeable query is out-of-sample"
    ///     claim stops being true and must be amended rather than quietly outgrown.
    /// </summary>
    [Fact]
    public void TuningQueryIds_StayEmpty()
    {
        RetrievalTuningSets.TuningQueryIds.ShouldBeEmpty(
            "nothing has been tuned on the public docs corpus. If a sweep now selects parameters " +
            "here, amend ADR-0090 (and ADR-0056) in the same change: the held-out gate stops " +
            "covering the whole catalog and the published numbers stop being out-of-sample.");
    }
}
