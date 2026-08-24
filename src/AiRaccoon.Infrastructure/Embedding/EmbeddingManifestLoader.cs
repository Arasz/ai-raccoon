using AiRaccoon.Infrastructure.Embedding.Manifest;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>Reads a model directory's manifest and turns it into the engine descriptor.</summary>
public interface IEmbeddingManifestLoader
{
    /// <summary>Loads, validates and maps <see cref="EmbeddingManifest.FileName" />; throws with an
    /// actionable message when the directory has no manifest, it fails the D1 contract, or a pinned
    /// file is missing from disk.</summary>
    EngineDescriptor Load(string modelDirectory);
}

/// <summary>
///     The one manifest read path (plan D1/D5). Parsing and validation belong to the WP1 contract —
///     <see cref="IEmbeddingManifestSerializer" /> and <see cref="IEmbeddingManifestValidator" /> —
///     so the schema is pinned in exactly one place; this type owns the file I/O, the per-file
///     sha256 pin check and the mapping onto <see cref="EngineDescriptor" />. The pin check answers
///     "should we run at all"; the separate D7 engine fingerprint (manifest bytes only) answers
///     "should we re-embed" — two questions, two mechanisms.
/// </summary>
public sealed class EmbeddingManifestLoader(
    IEmbeddingManifestSerializer serializer,
    IEmbeddingManifestValidator validator,
    IFileHasher? fileHasher = null) : IEmbeddingManifestLoader
{
    private readonly IFileHasher hasher = fileHasher ?? new FileHasher();

    public EngineDescriptor Load(string modelDirectory)
    {
        ArgumentNullException.ThrowIfNull(modelDirectory);
        var manifestPath = Path.Combine(modelDirectory, EmbeddingManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                $"Configured embedding model directory '{modelDirectory}' has no {EmbeddingManifest.FileName}. " +
                $"A local model directory must contain a {EmbeddingManifest.FileName} describing its files, tokenizer, " +
                "pooling and dimensions — run 'ai-raccoon model download <repo-id>' to create one, or point " +
                "embedding.model at a .onnx file for the legacy path.");
        }

        EmbeddingManifest manifest;
        try
        {
            manifest = serializer.Deserialize(File.ReadAllText(manifestPath));
        }
        catch (EmbeddingManifestFormatException ex)
        {
            throw new InvalidOperationException($"Manifest '{manifestPath}' is not a valid v1 manifest: {ex.Message}", ex);
        }

        var errors = validator.Validate(manifest);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Manifest '{manifestPath}' fails the v1 contract:{Environment.NewLine}  - " +
                string.Join($"{Environment.NewLine}  - ", errors));
        }

        var files = manifest.Tokenizer.Files.Concat(manifest.Onnx.Files).Concat(manifest.ProvenanceFiles ?? []).ToList();
        foreach (var file in files)
        {
            var path = ResolveFile(modelDirectory, file.Path, manifestPath);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"Manifest '{manifestPath}' declares file '{file.Path}' but it is missing from '{modelDirectory}'. " +
                    "Re-run 'ai-raccoon model download' to restore the model directory.");
            }

            var actual = hasher.Sha256OfFile(path);
            if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Manifest '{manifestPath}' pins '{file.Path}' to sha256 {file.Sha256}, but the file on disk hashes " +
                    $"to {actual}. The model directory may have been tampered with or partially replaced — re-run " +
                    "'ai-raccoon model download' to restore a clean copy.");
            }
        }

        return new EngineDescriptor(
            manifest.Model,
            manifest.Source.Repo,
            manifest.Source.Revision,
            manifest.Dimensions,
            manifest.ContextWindowTokens,
            SchemaName(manifest.Normalization),
            SchemaName(manifest.Pooling.Mode),
            EngineDescriptor.DefaultSpecialTokenReservation,
            SchemaName(manifest.Tokenizer.Family),
            manifest.Tokenizer.Files[0].Path,
            manifest.Tokenizer.Family == TokenizerFamily.SentencePiece
                ? new SentencePieceTokenizerOptions(
                    manifest.Tokenizer.Options?.AddBeginOfSentence ?? true,
                    manifest.Tokenizer.Options?.AddEndOfSentence ?? true,
                    manifest.Tokenizer.Options?.SpecialTokens,
                    manifest.Tokenizer.Options?.VocabOffset ?? 0)
                : null,
            manifest.RequiresTokenTypeIds,
            manifest.Onnx.Inputs,
            manifest.Onnx.TokenEmbeddingsOutput ?? manifest.Pooling.OutputNames?.TokenEmbeddings ?? "last_hidden_state",
            manifest.Onnx.EmbeddingOutput,
            manifest.Onnx.Files[0].Path,
            files);
    }

    /// <summary>Manifest file paths stay inside the model directory — never rooted, never escaping.</summary>
    private static string ResolveFile(string modelDirectory, string relativePath, string manifestPath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Manifest '{manifestPath}' declares file '{relativePath}'; file paths must be relative to the model directory.");
        }

        return Path.Combine(modelDirectory, relativePath);
    }

    private static string SchemaName<TEnum>(TEnum value) where TEnum : struct, Enum => KebabCaseEnumJsonConverter<TEnum>.SchemaNameOf(value);
}
