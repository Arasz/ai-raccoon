using AiRaccoon.Core.Chunking;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using Microsoft.ML.Tokenizers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     RAG-F3/RAG-F4 hard invariant (docs/adr/0036): chunking this repo's own docs/**/*.md, plus a
///     set of adversarial fixtures, through the production local-engine path must never emit a chunk
///     whose real BERT WordPiece length exceeds the model's usable content budget
///     (<see cref="OnnxEmbeddingGenerator.MaxContentTokens" /> = 254, reserving 2 for [CLS]/[SEP]).
///     This is asserted as a hard ceiling, not a percentage improvement.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ChunkingCorpusGuaranteeTests
{
    private const int MaxTokens = OnnxEmbeddingGenerator.MaxContentTokens;

    [Fact]
    public void ChunkingDocsCorpus_WithRealBertTokenizer_NoChunkExceedsTheContentBudget()
    {
        var (chunker, bert) = BuildRealLocalChunker();
        var repoRoot = FindRepoRoot();
        var files = Directory.EnumerateFiles(Path.Combine(repoRoot, "docs"), "*.md", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        files.ShouldNotBeEmpty();

        var totalChunks = 0;
        var overBudget = new List<(string File, int Tokens)>();
        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            var chunks = chunker.Chunk(content, MaxTokens, 48);
            foreach (var chunk in chunks)
            {
                totalChunks++;
                var tokens = bert.EncodeToIds(chunk, true, true, true).Count;
                if (tokens > 256)
                {
                    overBudget.Add((file, tokens));
                }
            }
        }

        overBudget.ShouldBeEmpty(
            $"{overBudget.Count}/{totalChunks} chunks exceed the model's 256-token window; " +
            $"worst: {(overBudget.Count > 0 ? overBudget.MaxBy(x => x.Tokens) : default)}");
    }

    /// <summary>
    ///     RAG-CHUNK-F1 hard invariant (docs/adr/0048): every chunk this repo's own docs corpus
    ///     produces is a well-formed markdown fragment — its fence state opens and closes inside the
    ///     chunk. A chunk that begins inside a code block has no opening marker in view, so
    ///     <see cref="HeadingPathParser" /> reads the block's '#' comments as level-1 headings and
    ///     that prose reaches the FTS-weighted section column. Measured RED at 72/4140 chunks.
    /// </summary>
    [Fact]
    public void ChunkingDocsCorpus_EveryChunkIsFenceBalanced()
    {
        var (chunker, _) = BuildRealLocalChunker();
        var repoRoot = FindRepoRoot();
        var files = Directory.EnumerateFiles(Path.Combine(repoRoot, "docs"), "*.md", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        files.ShouldNotBeEmpty();

        var totalChunks = 0;
        var unbalanced = new List<(string File, string Heading)>();
        foreach (var file in files)
        {
            foreach (var chunk in chunker.Chunk(File.ReadAllText(file), MaxTokens, 48))
            {
                totalChunks++;
                if (FenceDelimiterCount(chunk) % 2 != 0)
                {
                    unbalanced.Add((Path.GetFileName(file), HeadingPathParser.Parse(chunk)));
                }
            }
        }

        unbalanced.ShouldBeEmpty(
            $"{unbalanced.Count}/{totalChunks} chunks end in a different fence state than they began, so a " +
            $"boundary fell inside a code block; first: {(unbalanced.Count > 0 ? unbalanced[0] : default)}");
    }

    private static int FenceDelimiterCount(string chunk) =>
        chunk.Split('\n').Count(line =>
            line.TrimStart().StartsWith("```", StringComparison.Ordinal)
            || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal));

    [Fact]
    public void ChunkingHostileFixtures_NoChunkExceedsTheContentBudget()
    {
        var (chunker, bert) = BuildRealLocalChunker();

        foreach (var (name, text) in HostileFixtures())
        {
            var chunks = chunker.Chunk(text, MaxTokens, 48);
            chunks.ShouldAllBe(chunk => bert.EncodeToIds(chunk, true, true, true).Count <= 256,
                $"fixture '{name}' produced a chunk over the 256-token window");
        }
    }

    /// <summary>
    ///     Characterization test, not a gate (docs/adr/0036 — recorded finding, chunker-side
    ///     remediation deliberately out of scope for this wave; measured at 1/15,246 entries on the
    ///     live bank). \n, \t and \r are not word separators for this BertTokenizer configuration, so
    ///     a punctuation-free run of 100+ chars collapses to a single [UNK] instead of decomposing —
    ///     reporting a near-zero token count a ceiling check cannot see. This asserts the CURRENT
    ///     (wrong) behaviour's EXACT shape on purpose — [CLS] [UNK] [SEP], 3 ids, nothing looser — so
    ///     it is GREEN today and stays green while the gap stays visible and pinned. A fix that makes
    ///     this collapse stop must make this specific assertion fail; when that happens, invert it
    ///     (don't loosen or delete it) and update the ADR.
    /// </summary>
    [Fact]
    public void NewlineSeparatedHashList_StillCollapsesToUnknown_DocumentsKnownGap()
    {
        var bert = OnnxEmbeddingGenerator.CreateTokenizer(BundledModel.ResolveVocabPath());
        var hashList = string.Join("\n", Enumerable.Range(0, 60)
            .Select(i => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i)))));

        var ids = bert.EncodeToIds(hashList, true, true, true);

        ids.Count.ShouldBe(3,
            "known gap (docs/adr/0036): newline-joined hex content is expected to still collapse to exactly " +
            "[CLS] [UNK] [SEP] (3 ids); if this now fails, the gap may be fixed — invert this assertion and update the ADR");
    }

    private static IEnumerable<(string Name, string Text)> HostileFixtures()
    {
        yield return ("hex/base64 blob", string.Join(" ", Enumerable.Range(0, 40)
            .Select(i => Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i))))));

        yield return ("minified JSON line", "{" + string.Join(",", Enumerable.Range(0, 300)
            .Select(i => $"\"key{i}\":\"value{i}withSomeAdditionalPaddingTextToMakeThisRealisticallyLong\"")) + "}");

        yield return ("CJK paragraph", string.Concat(Enumerable.Repeat(
            "这是一个用于测试的中文段落,包含很多汉字,用来验证分块器在处理连续的中日韩文字时是否会产生超过预算的分块。", 40)));

        yield return ("unbalanced fence", $"# Notes\n\nSee the fence marker below.\n\n```\n{string.Join("\n", Enumerable.Range(1, 200).Select(i => $"line {i} of unterminated fenced content with some words."))}\n");
    }

    private static (IMarkdownChunker Chunker, BertTokenizer Bert) BuildRealLocalChunker()
    {
        var vocabPath = BundledModel.ResolveVocabPath();
        var bert = OnnxEmbeddingGenerator.CreateTokenizer(vocabPath);
        var chunker = new MarkdownChunker(text => bert.CountTokens(text));
        return (chunker, bert);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "docs")) && File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("repo root not found");
    }
}
