using System.Text.Json;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     The golden file and the corpus it was captured from are ONE datum: the golden records the
///     vector of a corpus query, so the two can never be edited apart. A rename that moves the
///     corpus's questions without re-capturing leaves the golden internally consistent with
///     nothing — and the failure is invisible to CI, which has no model to compare against.
///     <para>
///         This gate needs no model: it only asks whether the golden's question text still IS the
///         corpus's question text, entry for entry. It would not have caught a partial rename
///         (both files renamed together), but it does catch the corpus moving under a golden
///         that was not re-captured.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class GoldenCorpusParityTests
{
    private const string CorpusRelativePath = "scripts/retrieval_tuning/corpora/eval-set-100.json";
    private const string GoldenRelativePath = "tests/AiRaccoon.Tests/Resources/goldens/minilm-eval-set-100.json";

    [Fact]
    public void TheGoldenQuestions_AreTheCorpusQuestions()
    {
        var corpus = JsonDocument.Parse(ReadRepoFile(CorpusRelativePath)).RootElement;
        var golden = JsonDocument.Parse(ReadRepoFile(GoldenRelativePath)).RootElement;

        var corpusQueries = corpus.GetProperty("queries").EnumerateArray()
            .ToDictionary(entry => entry.GetProperty("id").GetString()!,
                entry => entry.GetProperty("query").GetString()!);
        var goldenQueries = golden.GetProperty("Entries").EnumerateArray()
            .ToDictionary(entry => entry.GetProperty("Id").GetString()!,
                entry => entry.GetProperty("Query").GetString()!);

        goldenQueries.Keys.ShouldBe(
            corpusQueries.Keys,
            "the golden covers exactly the corpus's queries - a query in one and not the other is a capture that no longer matches");

        var mismatched = corpusQueries
            .Where(pair => goldenQueries[pair.Key] != pair.Value)
            .Select(pair => $"{pair.Key}: corpus '{pair.Value}' vs golden '{goldenQueries[pair.Key]}'")
            .ToList();

        mismatched.ShouldBeEmpty(
            "the golden is a CAPTURE of the corpus: a query text and its vector cannot be edited apart. " +
            "Re-capture the golden (AIRACCOON_GOLDEN_CAPTURE=1) after any change to the corpus's questions");
    }

    private static string ReadRepoFile(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new InvalidOperationException($"Could not locate {relativePath} from the test output directory.");
    }
}
