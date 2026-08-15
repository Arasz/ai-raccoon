using AiRaccoon.Infrastructure.Embedding;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     WP3 step 4's baseline. The acceptance criterion is stated in WordPiece tokens — "post-backfill,
///     the count of entries over 254 WordPiece tokens is zero" — and nothing in SQL can count those,
///     so the number has to come from the same tokenizer the chunker uses or it is not the same number.
///     <para>
///         Points at whatever bank <see cref="BankEnvVar" /> names and asserts nothing about the
///         product. Run it, read the numbers, record them. Read-only: it opens the file and only
///         SELECTs.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class OverWindowRowProbe
{
    internal const string BankEnvVar = "AIRACCOON_OVERWINDOW_PROBE_BANK";

    private readonly ITestOutputHelper _output;

    public OverWindowRowProbe(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Probe_CountsRowsOverTheEmbeddingWindow()
    {
        var bank = Environment.GetEnvironmentVariable(BankEnvVar);
        if (bank is null)
        {
            _output.WriteLine(
                $"{BankEnvVar} not set — this probe counts rows whose WordPiece length exceeds the "
                + "embedding window (WP3 step 4's baseline and its post-backfill check). Set it to a bank path to run it.");
            return;
        }

        File.Exists(bank).ShouldBeTrue($"{BankEnvVar} must name an existing bank file");

        await using var connection = new SqliteConnection($"Data Source={bank};Mode=ReadOnly");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var provider = await connection.ExecuteScalarAsync<string?>(
            "SELECT value FROM settings WHERE key = 'embedding.provider'");
        var model = await connection.ExecuteScalarAsync<string?>(
            "SELECT value FROM settings WHERE key = 'embedding.model'");
        provider = string.IsNullOrWhiteSpace(provider) ? "local" : provider;

        // The same budget the ingest path resolves, read from the same place, so the count is
        // comparable to what the chunker would produce rather than to a guess about it.
        var budget = EmbeddingService.SafeChunkBudgetFor(provider, model);
        var tokenizer = OnnxEmbeddingGenerator.CreateTokenizer(BundledModel.ResolveVocabPath());

        List<(long Id, string? SourceFile, string Value)> rows =
        [
            .. await connection.QueryAsync<(long Id, string? SourceFile, string Value)>(
                "SELECT id AS Id, source_file AS SourceFile, value AS Value FROM entries WHERE value IS NOT NULL")
        ];

        var over = rows.Select(r => (r.Id, r.SourceFile, Tokens: tokenizer.CountTokens(r.Value)))
            .Where(r => r.Tokens > budget)
            .ToList();
        var overDocuments = over.Where(r => r.SourceFile is not null)
            .Select(r => r.SourceFile!).Distinct(StringComparer.Ordinal).Count();

        _output.WriteLine($"bank            : {bank}");
        _output.WriteLine($"provider/model  : {provider} / {model ?? "(unset)"}");
        _output.WriteLine($"window (tokens) : {budget}");
        _output.WriteLine($"rows            : {rows.Count}");
        _output.WriteLine($"over-window rows: {over.Count} ({100.0 * over.Count / Math.Max(1, rows.Count):F1}%)");
        _output.WriteLine($"  of those, no source_file: {over.Count(r => r.SourceFile is null)}");
        _output.WriteLine($"documents holding one     : {overDocuments}");
        _output.WriteLine($"worst row       : {(over.Count == 0 ? 0 : over.Max(r => r.Tokens))} tokens");
        _output.WriteLine($"tokens unembedded (sum of excess): {over.Sum(r => (long)(r.Tokens - budget))}");
    }
}
