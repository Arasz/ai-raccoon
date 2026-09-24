using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     Byte-Pair-Encoding <see cref="IEmbeddingTokenizer" /> driven entirely by a Hugging Face
///     <c>tokenizer.json</c> — the "tokenizer-json" family (byte-level BPE: ModernBERT/granite,
///     jina-v2-code, Qwen3; sentencepiece-style BPE with byte fallback: EmbeddingGemma). Every
///     mechanic (byte-to-unicode alphabet, merge ranks, added-token protection, post-processor
///     wrapping) is read from the file itself, never guessed — an unrecognized shape throws rather
///     than approximating.
/// </summary>
public sealed class TokenizerJsonEmbeddingTokenizer : IEmbeddingTokenizer
{
    private readonly IContentEncoder _contentEncoder;
    private readonly AddedTokenSplitter _addedTokens;
    private readonly IReadOnlyList<int> _prependIds;
    private readonly IReadOnlyList<int> _appendIds;

    private TokenizerJsonEmbeddingTokenizer(IContentEncoder contentEncoder, AddedTokenSplitter addedTokens,
        IReadOnlyList<int> prependIds, IReadOnlyList<int> appendIds)
    {
        _contentEncoder = contentEncoder;
        _addedTokens = addedTokens;
        _prependIds = prependIds;
        _appendIds = appendIds;
    }

    /// <summary>Parses <paramref name="tokenizerJsonPath" /> once and builds the encoder for the
    /// shape it declares (model.type, normalizer, pre_tokenizer, post_processor).</summary>
    public static TokenizerJsonEmbeddingTokenizer Create(string tokenizerJsonPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(tokenizerJsonPath));
        var root = document.RootElement;

        var model = root.GetProperty("model");
        var modelType = model.GetProperty("type").GetString();
        IContentEncoder contentEncoder = modelType switch
        {
            "BPE" => BuildBpeEncoder(root, model, tokenizerJsonPath),
            "WordPiece" => BuildWordPieceEncoder(root, model, tokenizerJsonPath),
            var other => throw new NotSupportedException(
                $"tokenizer.json model.type '{other}' is not supported by the tokenizer-json family (BPE and WordPiece are); '{tokenizerJsonPath}'.")
        };

        var addedTokens = AddedTokenSplitter.From(root);
        var (prependIds, appendIds) = PostProcessorOf(root);

        return new TokenizerJsonEmbeddingTokenizer(contentEncoder, addedTokens, prependIds, appendIds);
    }

    private static IContentEncoder BuildBpeEncoder(JsonElement root, JsonElement model, string tokenizerJsonPath)
    {
        var vocab = ReadVocab(model);
        var mergeRanks = ReadMerges(model);
        var byteFallback = model.TryGetProperty("byte_fallback", out var bf) && bf.ValueKind == JsonValueKind.True;
        var normalizer = NormalizerOf(root);

        return PreTokenizerOf(root, byteFallback) switch
        {
            PreTokenizerKind.ByteLevelGpt2Kind => new BpeContentEncoder(normalizer, Segmenters.Gpt2ByteLevel, vocab, mergeRanks, byteFallback: false),
            PreTokenizerKind.SequenceSplitThenByteLevel seq => new BpeContentEncoder(normalizer, Segmenters.SplitByRegexThenByteLevel(seq.Pattern), vocab, mergeRanks, byteFallback: false),
            PreTokenizerKind.SentencePieceSplitKind => new BpeContentEncoder(normalizer, Segmenters.WholeTextAsOneUnit, vocab, mergeRanks, byteFallback),
            var other => throw new NotSupportedException($"tokenizer.json pre_tokenizer shape '{other}' is not supported; '{tokenizerJsonPath}'.")
        };
    }

    private static IContentEncoder BuildWordPieceEncoder(JsonElement root, JsonElement model, string tokenizerJsonPath)
    {
        if (!root.TryGetProperty("pre_tokenizer", out var preTokenizer)
            || preTokenizer.GetProperty("type").GetString() != "BertPreTokenizer")
        {
            throw new NotSupportedException(
                $"tokenizer.json WordPiece model requires pre_tokenizer.type 'BertPreTokenizer'; '{tokenizerJsonPath}'.");
        }

        var vocab = ReadVocab(model);
        var unkToken = model.GetProperty("unk_token").GetString()!;
        var continuingPrefix = model.TryGetProperty("continuing_subword_prefix", out var prefix) && prefix.ValueKind == JsonValueKind.String
            ? prefix.GetString()!
            : "##";
        var maxCharsPerWord = model.TryGetProperty("max_input_chars_per_word", out var max) && max.ValueKind == JsonValueKind.Number
            ? max.GetInt32()
            : 100;

        var normalizerElement = root.GetProperty("normalizer");
        if (normalizerElement.GetProperty("type").GetString() != "BertNormalizer")
        {
            throw new NotSupportedException($"tokenizer.json WordPiece model requires normalizer.type 'BertNormalizer'; '{tokenizerJsonPath}'.");
        }

        var lowercase = BoolOrDefault(normalizerElement, "lowercase", true);
        var handleChineseChars = BoolOrDefault(normalizerElement, "handle_chinese_chars", true);
        var cleanText = BoolOrDefault(normalizerElement, "clean_text", true);
        var stripAccents = normalizerElement.TryGetProperty("strip_accents", out var strip) && strip.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? strip.GetBoolean()
            : lowercase; // HF's own default: unset strip_accents follows lowercase.

        return new WordPieceContentEncoder(vocab, unkToken, continuingPrefix, maxCharsPerWord, lowercase, handleChineseChars, cleanText, stripAccents);
    }

    private static bool BoolOrDefault(JsonElement element, string property, bool defaultValue) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;

    /// <summary>Content tokens only — never the post-processor's prepend/append specials.</summary>
    public int CountTokens(string text) => EncodeToIds(text, addSpecialTokens: false).Count;

    public IReadOnlyList<int> EncodeToIds(string text, bool addSpecialTokens)
    {
        ArgumentNullException.ThrowIfNull(text);
        var contentIds = new List<int>();
        foreach (var span in _addedTokens.Split(text))
        {
            if (span.Id is { } id)
            {
                contentIds.Add(id);
            }
            else
            {
                contentIds.AddRange(_contentEncoder.Encode(span.Text));
            }
        }

        if (!addSpecialTokens)
        {
            return contentIds;
        }

        var wrapped = new List<int>(_prependIds.Count + contentIds.Count + _appendIds.Count);
        wrapped.AddRange(_prependIds);
        wrapped.AddRange(contentIds);
        wrapped.AddRange(_appendIds);
        return wrapped;
    }

    /// <summary>What the post-processor's "single" template adds beyond the content sequence.</summary>
    public int SpecialTokenReservation => _prependIds.Count + _appendIds.Count;

    private static Dictionary<string, int> ReadVocab(JsonElement model)
    {
        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in model.GetProperty("vocab").EnumerateObject())
        {
            vocab[property.Name] = property.Value.GetInt32();
        }

        return vocab;
    }

    private static Dictionary<(string First, string Second), int> ReadMerges(JsonElement model)
    {
        var ranks = new Dictionary<(string, string), int>();
        if (!model.TryGetProperty("merges", out var merges) || merges.ValueKind != JsonValueKind.Array)
        {
            return ranks;
        }

        var rank = 0;
        foreach (var merge in merges.EnumerateArray())
        {
            var pair = merge.ValueKind == JsonValueKind.Array
                ? (merge[0].GetString()!, merge[1].GetString()!)
                : SplitMergeString(merge.GetString()!);
            ranks[pair] = rank++;
        }

        return ranks;
    }

    private static (string, string) SplitMergeString(string merge)
    {
        var space = merge.IndexOf(' ');
        return (merge[..space], merge[(space + 1)..]);
    }

    private static Func<string, string> NormalizerOf(JsonElement root)
    {
        if (!root.TryGetProperty("normalizer", out var normalizer) || normalizer.ValueKind == JsonValueKind.Null)
        {
            return static text => text;
        }

        if (normalizer.GetProperty("type").GetString() == "NFC")
        {
            return static text => text.Normalize(NormalizationForm.FormC);
        }

        if (normalizer.GetProperty("type").GetString() == "Replace"
            && normalizer.GetProperty("pattern").TryGetProperty("String", out var patternElement))
        {
            // Extracted eagerly: the closure must not hold JsonElements past Create's `using` scope.
            var pattern = patternElement.GetString()!;
            var content = normalizer.GetProperty("content").GetString();
            return text => text.Replace(pattern, content, StringComparison.Ordinal);
        }

        throw new NotSupportedException($"tokenizer.json normalizer.type '{normalizer.GetProperty("type").GetString()}' is not supported.");
    }

    private abstract record PreTokenizerKind
    {
        public sealed record ByteLevelGpt2Kind : PreTokenizerKind;

        public sealed record SequenceSplitThenByteLevel(string Pattern) : PreTokenizerKind;

        public sealed record SentencePieceSplitKind : PreTokenizerKind;

        public static readonly PreTokenizerKind ByteLevelGpt2 = new ByteLevelGpt2Kind();
        public static readonly PreTokenizerKind SentencePieceSplit = new SentencePieceSplitKind();
    }

    private static PreTokenizerKind PreTokenizerOf(JsonElement root, bool byteFallback)
    {
        if (!root.TryGetProperty("pre_tokenizer", out var preTokenizer) || preTokenizer.ValueKind == JsonValueKind.Null)
        {
            throw new NotSupportedException("tokenizer.json has no pre_tokenizer; the tokenizer-json family requires one.");
        }

        var type = preTokenizer.GetProperty("type").GetString();
        switch (type)
        {
            case "ByteLevel":
                return PreTokenizerKind.ByteLevelGpt2;
            case "Split" when byteFallback:
                return PreTokenizerKind.SentencePieceSplit;
            case "Sequence":
                {
                    var steps = preTokenizer.GetProperty("pretokenizers").EnumerateArray().ToList();
                    var split = steps.FirstOrDefault(s => s.GetProperty("type").GetString() == "Split");
                    var hasByteLevel = steps.Any(s => s.GetProperty("type").GetString() == "ByteLevel");
                    if (split.ValueKind == JsonValueKind.Object && hasByteLevel)
                    {
                        return new PreTokenizerKind.SequenceSplitThenByteLevel(split.GetProperty("pattern").GetProperty("Regex").GetString()!);
                    }

                    throw new NotSupportedException("tokenizer.json pre_tokenizer Sequence must contain a Split(Regex) step and a ByteLevel step.");
                }
            default:
                throw new NotSupportedException($"tokenizer.json pre_tokenizer.type '{type}' is not supported.");
        }
    }

    private static (IReadOnlyList<int> Prepend, IReadOnlyList<int> Append) PostProcessorOf(JsonElement root)
    {
        if (!root.TryGetProperty("post_processor", out var postProcessor) || postProcessor.ValueKind == JsonValueKind.Null)
        {
            return ([], []);
        }

        return ProcessorIds(postProcessor);
    }

    private static (IReadOnlyList<int>, IReadOnlyList<int>) ProcessorIds(JsonElement processor) =>
        processor.GetProperty("type").GetString() switch
        {
            "TemplateProcessing" => TemplateProcessingIds(processor),
            "RobertaProcessing" => ([processor.GetProperty("cls")[1].GetInt32()], [processor.GetProperty("sep")[1].GetInt32()]),
            "Sequence" => ProcessorIds(FindTemplateInSequence(processor)),
            var other => throw new NotSupportedException($"tokenizer.json post_processor.type '{other}' is not supported.")
        };

    private static JsonElement FindTemplateInSequence(JsonElement sequence)
    {
        foreach (var processor in sequence.GetProperty("processors").EnumerateArray())
        {
            var type = processor.GetProperty("type").GetString();
            if (type is "TemplateProcessing" or "RobertaProcessing")
            {
                return processor;
            }
        }

        throw new NotSupportedException("tokenizer.json post_processor Sequence has no TemplateProcessing/RobertaProcessing step.");
    }

    private static (IReadOnlyList<int>, IReadOnlyList<int>) TemplateProcessingIds(JsonElement template)
    {
        var specialTokens = template.GetProperty("special_tokens");
        var prepend = new List<int>();
        var append = new List<int>();
        var seenSequence = false;
        foreach (var step in template.GetProperty("single").EnumerateArray())
        {
            if (step.TryGetProperty("Sequence", out _))
            {
                seenSequence = true;
                continue;
            }

            var id = step.GetProperty("SpecialToken").GetProperty("id").GetString()!;
            var numericId = specialTokens.GetProperty(id).GetProperty("ids")[0].GetInt32();
            (seenSequence ? append : prepend).Add(numericId);
        }

        return (prepend, append);
    }

    /// <summary>The Unicode-codepoint-vs-byte-fallback, regex-vs-whole-unit segmentation strategies
    /// a <see cref="ContentEncoder" /> can be built with — pure functions, no state.</summary>
    private static class Segmenters
    {
        /// <summary>GPT-2's own default pretokenizer regex — the `tokenizers` crate's ByteLevel
        /// pretokenizer applies exactly this when built without a separate Split step.</summary>
        private static readonly Regex Gpt2Pattern = new(
            "'s|'t|'re|'ve|'m|'ll|'d| ?\\p{L}+| ?\\p{N}+| ?[^\\s\\p{L}\\p{N}]+|\\s+(?!\\S)|\\s+",
            RegexOptions.Compiled);

        public static IEnumerable<string> Gpt2ByteLevel(string text) => RegexSegments(Gpt2Pattern, text);

        public static Func<string, IEnumerable<string>> SplitByRegexThenByteLevel(string pattern)
        {
            var regex = new Regex(pattern, RegexOptions.Compiled);
            return text => RegexSegments(regex, text);
        }

        public static IEnumerable<string> WholeTextAsOneUnit(string text)
        {
            if (text.Length > 0)
            {
                yield return text;
            }
        }

        private static IEnumerable<string> RegexSegments(Regex regex, string text)
        {
            foreach (Match match in regex.Matches(text))
            {
                yield return match.Value;
            }
        }
    }

    /// <summary>Turns raw text into content-token ids for one tokenizer.json model shape.</summary>
    private interface IContentEncoder
    {
        IReadOnlyList<int> Encode(string text);
    }

    /// <summary>
    ///     Turns one pre-tokenized segment into BPE symbols, either GPT-2 byte-level (every byte of
    ///     the segment's UTF-8 remapped to the 256-entry printable alphabet) or sentencepiece-style
    ///     (one symbol per Unicode codepoint, expanded to `&lt;0xNN&gt;` byte-fallback symbols for a
    ///     codepoint the vocabulary does not carry on its own), then greedily merges by rank.
    /// </summary>
    private sealed class BpeContentEncoder(Func<string, string> normalize, Func<string, IEnumerable<string>> segment,
        IReadOnlyDictionary<string, int> vocab, IReadOnlyDictionary<(string, string), int> mergeRanks, bool byteFallback) : IContentEncoder
    {
        public IReadOnlyList<int> Encode(string text)
        {
            var ids = new List<int>();
            var normalized = normalize(text);
            foreach (var piece in segment(normalized))
            {
                var symbols = byteFallback ? ByteFallbackSymbols(piece) : ByteLevelAlphabet.ToByteChars(piece);
                foreach (var symbol in Bpe.Merge(symbols, mergeRanks))
                {
                    ids.Add(vocab[symbol]);
                }
            }

            return ids;
        }

        private List<string> ByteFallbackSymbols(string piece)
        {
            var symbols = new List<string>();
            var index = 0;
            while (index < piece.Length)
            {
                var codepointLength = char.IsSurrogatePair(piece, index) ? 2 : 1;
                var codepoint = piece.Substring(index, codepointLength);
                index += codepointLength;

                if (vocab.ContainsKey(codepoint))
                {
                    symbols.Add(codepoint);
                    continue;
                }

                foreach (var b in Encoding.UTF8.GetBytes(codepoint))
                {
                    symbols.Add($"<0x{b:X2}>");
                }
            }

            return symbols;
        }
    }

    /// <summary>
    ///     BertNormalizer + BertPreTokenizer + greedy-longest-match WordPiece, read entirely from
    ///     the tokenizer.json model/normalizer blocks — the classic BERT algorithm, ported here
    ///     because <see cref="WordPieceEmbeddingTokenizer" />'s ML.Tokenizers options are tuned for
    ///     the bundled model and do not reproduce every real repo's punctuation/CJK behavior
    ///     exactly (parity-measured, not assumed).
    /// </summary>
    private sealed class WordPieceContentEncoder(
        IReadOnlyDictionary<string, int> vocab, string unkToken, string continuingPrefix, int maxCharsPerWord,
        bool lowercase, bool handleChineseChars, bool cleanText, bool stripAccents) : IContentEncoder
    {
        private readonly int _unkId = vocab[unkToken];

        public IReadOnlyList<int> Encode(string text)
        {
            var normalized = Normalize(text);
            var ids = new List<int>();
            foreach (var word in BertPreTokenize(normalized))
            {
                ids.AddRange(TokenizeWord(word));
            }

            return ids;
        }

        private string Normalize(string text)
        {
            var result = cleanText ? CleanText(text) : text;
            result = handleChineseChars ? HandleChineseChars(result) : result;
            if (lowercase)
            {
                result = result.ToLowerInvariant();
            }

            return stripAccents ? StripAccents(result) : result;
        }

        private IReadOnlyList<int> TokenizeWord(string word)
        {
            if (word.Length == 0)
            {
                return [];
            }

            if (word.Length > maxCharsPerWord)
            {
                return [_unkId];
            }

            var ids = new List<int>();
            var start = 0;
            while (start < word.Length)
            {
                var end = word.Length;
                string? matched = null;
                while (end > start)
                {
                    var candidate = start > 0 ? continuingPrefix + word[start..end] : word[start..end];
                    if (vocab.TryGetValue(candidate, out var id))
                    {
                        matched = candidate;
                        ids.Add(id);
                        break;
                    }

                    end--;
                }

                if (matched is null)
                {
                    return [_unkId];
                }

                start = end;
            }

            return ids;
        }

        private static string CleanText(string text)
        {
            var builder = new StringBuilder(text.Length);
            foreach (var ch in text)
            {
                if (ch == '\0' || ch == '�' || IsControlNotWhitespace(ch))
                {
                    continue;
                }

                builder.Append(IsBertWhitespace(ch) ? ' ' : ch);
            }

            return builder.ToString();
        }

        private static bool IsControlNotWhitespace(char ch) =>
            ch is not ('\t' or '\n' or '\r')
            && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format;

        private static bool IsBertWhitespace(char ch) =>
            ch is ' ' or '\t' or '\n' or '\r'
            || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.SpaceSeparator;

        private static string HandleChineseChars(string text)
        {
            var builder = new StringBuilder(text.Length);
            foreach (var rune in text.EnumerateRunes())
            {
                if (IsChineseChar(rune.Value))
                {
                    builder.Append(' ').Append(rune.ToString()).Append(' ');
                }
                else
                {
                    builder.Append(rune.ToString());
                }
            }

            return builder.ToString();
        }

        /// <summary>The CJK Unified Ideograph blocks BERT's BasicTokenizer isolates — deliberately
        /// not Hangul/Hiragana/Katakana, which BERT tokenizes as ordinary characters.</summary>
        private static bool IsChineseChar(int codepoint) =>
            codepoint is >= 0x4E00 and <= 0x9FFF
                or >= 0x3400 and <= 0x4DBF
                or >= 0x20000 and <= 0x2A6DF
                or >= 0x2A700 and <= 0x2B73F
                or >= 0x2B740 and <= 0x2B81F
                or >= 0x2B820 and <= 0x2CEAF
                or >= 0xF900 and <= 0xFAFF
                or >= 0x2F800 and <= 0x2FA1F;

        private static string StripAccents(string text)
        {
            var decomposed = text.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var ch in decomposed)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString();
        }

        private static IEnumerable<string> BertPreTokenize(string text)
        {
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var piece in SplitPunctuation(word))
                {
                    yield return piece;
                }
            }
        }

        private static IEnumerable<string> SplitPunctuation(string word)
        {
            var current = new StringBuilder();
            foreach (var ch in word)
            {
                if (!IsPunctuation(ch))
                {
                    current.Append(ch);
                    continue;
                }

                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                yield return ch.ToString();
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
            }
        }

        private static bool IsPunctuation(char ch)
        {
            var code = (int)ch;
            if (code is >= 33 and <= 47 or >= 58 and <= 64 or >= 91 and <= 96 or >= 123 and <= 126)
            {
                return true;
            }

            return System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) is
                System.Globalization.UnicodeCategory.ConnectorPunctuation or
                System.Globalization.UnicodeCategory.DashPunctuation or
                System.Globalization.UnicodeCategory.OpenPunctuation or
                System.Globalization.UnicodeCategory.ClosePunctuation or
                System.Globalization.UnicodeCategory.InitialQuotePunctuation or
                System.Globalization.UnicodeCategory.FinalQuotePunctuation or
                System.Globalization.UnicodeCategory.OtherPunctuation;
        }
    }

    /// <summary>The GPT-2 byte&lt;-&gt;unicode alphabet: printable bytes map to themselves, the
    /// remaining 68 map to private-use code points starting at U+0100 — the same table
    /// `tokenizers`' ByteLevel pretokenizer uses, so a byte-level vocab's keys line up.</summary>
    private static class ByteLevelAlphabet
    {
        private static readonly char[] Table = Build();

        public static List<string> ToByteChars(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var chars = new char[bytes.Length];
            for (var i = 0; i < bytes.Length; i++)
            {
                chars[i] = Table[bytes[i]];
            }

            var symbols = new List<string>(chars.Length);
            foreach (var c in chars)
            {
                symbols.Add(c.ToString());
            }

            return symbols;
        }

        private static char[] Build()
        {
            var printable = new HashSet<int>();
            for (var b = '!'; b <= '~'; b++)
            {
                printable.Add(b);
            }

            for (var b = 0xA1; b <= 0xAC; b++)
            {
                printable.Add(b);
            }

            for (var b = 0xAE; b <= 0xFF; b++)
            {
                printable.Add(b);
            }

            var table = new char[256];
            var next = 256;
            for (var b = 0; b < 256; b++)
            {
                table[b] = (char)(printable.Contains(b) ? b : next++);
            }

            return table;
        }
    }

    /// <summary>Greedy BPE: repeatedly merge the lowest-rank adjacent pair until none remain.</summary>
    private static class Bpe
    {
        public static List<string> Merge(List<string> symbols, IReadOnlyDictionary<(string, string), int> ranks)
        {
            while (symbols.Count > 1)
            {
                var bestRank = int.MaxValue;
                var bestIndex = -1;
                for (var i = 0; i < symbols.Count - 1; i++)
                {
                    if (ranks.TryGetValue((symbols[i], symbols[i + 1]), out var rank) && rank < bestRank)
                    {
                        bestRank = rank;
                        bestIndex = i;
                    }
                }

                if (bestIndex < 0)
                {
                    break;
                }

                symbols[bestIndex] += symbols[bestIndex + 1];
                symbols.RemoveAt(bestIndex + 1);
            }

            return symbols;
        }
    }

    /// <summary>Splits raw text on the exact-match spans of every added token's content, so an
    /// added token is passed straight through as its own id instead of reaching the BPE encoder.</summary>
    private sealed class AddedTokenSplitter(Regex? pattern, IReadOnlyDictionary<string, int> idsByContent)
    {
        public static AddedTokenSplitter From(JsonElement root)
        {
            if (!root.TryGetProperty("added_tokens", out var addedTokens) || addedTokens.ValueKind != JsonValueKind.Array)
            {
                return new AddedTokenSplitter(null, new Dictionary<string, int>());
            }

            var idsByContent = new Dictionary<string, int>(StringComparer.Ordinal);
            var contents = new List<string>();
            foreach (var token in addedTokens.EnumerateArray())
            {
                var content = token.GetProperty("content").GetString()!;
                idsByContent[content] = token.GetProperty("id").GetInt32();
                contents.Add(content);
            }

            if (contents.Count == 0)
            {
                return new AddedTokenSplitter(null, idsByContent);
            }

            // Longest-first so e.g. "<|im_start|>" is never shadowed by a shorter overlapping token.
            var alternation = string.Join('|', contents.OrderByDescending(c => c.Length).Select(Regex.Escape));
            return new AddedTokenSplitter(new Regex(alternation, RegexOptions.Compiled), idsByContent);
        }

        public IEnumerable<(string Text, int? Id)> Split(string text)
        {
            if (pattern is null || text.Length == 0)
            {
                if (text.Length > 0)
                {
                    yield return (text, null);
                }

                yield break;
            }

            var cursor = 0;
            foreach (Match match in pattern.Matches(text))
            {
                if (match.Index > cursor)
                {
                    yield return (text[cursor..match.Index], null);
                }

                yield return (match.Value, idsByContent[match.Value]);
                cursor = match.Index + match.Length;
            }

            if (cursor < text.Length)
            {
                yield return (text[cursor..], null);
            }
        }
    }
}
