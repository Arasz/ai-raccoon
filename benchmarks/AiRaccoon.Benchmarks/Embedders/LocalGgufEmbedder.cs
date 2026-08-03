using AiRaccoon.Benchmarks.Corpus;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using System.Runtime.InteropServices;

namespace AiRaccoon.Benchmarks.Embedders;

/// <summary>
/// The production retrieval path: a real SqliteMemoryStore over the local GGUF model
/// (sqlite-memory's llama.cpp engine). The model path comes from AIRACCOON_TEST_GGUF
/// (same variable that gates the embedding integration tests). Results map back to corpus
/// document ids via the unique snippet text.
/// </summary>
public sealed class LocalGgufEmbedder : IEmbedder, IDisposable
{
    private readonly string _dataRoot;
    private readonly string _modelPath;
    private SqliteMemoryStore? _store;

    public LocalGgufEmbedder(string? modelPath = null, string? dataRoot = null)
    {
        _modelPath = modelPath ?? Environment.GetEnvironmentVariable("AIRACCOON_TEST_GGUF")
            ?? throw new InvalidOperationException(
                "AIRACCOON_TEST_GGUF is not set; pass the GGUF path or run scripts/download-embedding-model.sh first.");
        _dataRoot = dataRoot ?? CreateTempRoot();
    }

    public string Name => $"local:{Path.GetFileName(_modelPath)}";

    /// <summary>Embedding dimensions read from the GGUF file header (tensor n_dims metadata).</summary>
    public int Dimensions { get; private set; }

    public async Task IndexAsync(IReadOnlyList<CorpusDocument> documents,
        CancellationToken cancellationToken = default)
    {
        Dimensions = ReadGgufDimensions(_modelPath);
        var options = new InfrastructureOptions
        {
            DataRoot = _dataRoot,
            Rid = RuntimeInformation.RuntimeIdentifier,
        };
        CopyProvisionedExtensions(options);

        var factory = new SqliteConnectionFactory(options);
        _store = new SqliteMemoryStore(factory);

        await _store.ConfigureEmbeddingAsync("bench", "local", _modelPath, null, cancellationToken);
        foreach (var doc in documents)
        {
            await _store.WriteAsync(new MemoryWriteRequest("bench", doc.Text), cancellationToken);
        }
    }

    public async Task<IReadOnlyList<RetrievalHit>> SearchAsync(string query, int topK,
        CancellationToken cancellationToken = default)
    {
        var store = _store ?? throw new InvalidOperationException("IndexAsync must run before SearchAsync.");
        var results = await store.SearchAsync(
            new SearchQuery("bench", query, scope: SearchScope.Project, limit: topK), cancellationToken);

        return results
            .Select(r => new RetrievalHit(FindDocumentId(r.Snippet), r.Ranking))
            .ToList();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }

    private static string FindDocumentId(string snippet)
    {
        var doc = BenchmarkCorpus.Documents.FirstOrDefault(d => snippet.Contains(d.Body, StringComparison.Ordinal));
        return doc?.Id ?? snippet; // unmapped: report the snippet so failures are visible
    }

    /// <summary>
    /// Reads the output embedding dimension from a GGUF header: magic + version + tensor count,
    /// then the first metadata key/value pair whose key is "output.weight" (n_dims, dim_0).
    /// </summary>
    private static int ReadGgufDimensions(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        var magic = reader.ReadUInt32();
        if (magic != 0x46554747) // "GGUF"
        {
            return 0;
        }

        reader.ReadUInt32(); // version
        reader.ReadUInt64(); // tensor count
        var kvCount = reader.ReadUInt64();
        for (ulong i = 0; i < kvCount; i++)
        {
            var key = ReadGgufString(reader);
            var type = reader.ReadUInt32();
            if (type == 9 && key == "output.weight") // 9 = ARRAY (llama.cpp gguf_type)
            {
                var elementType = reader.ReadUInt32();
                var n = reader.ReadUInt64();
                if (elementType != 4) // 4 = UINT32 (n_dims, dim_0, ...)
                {
                    return 0;
                }

                var values = new uint[n];
                for (ulong j = 0; j < n; j++)
                {
                    values[j] = reader.ReadUInt32();
                }

                return (int)(n > 1 ? values[1] : values[0]); // [n_dims, dim_0, ...]
            }

            SkipGgufValue(reader, type);
        }

        return 0;
    }

    private static string ReadGgufString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();
        return System.Text.Encoding.UTF8.GetString(reader.ReadBytes((int)length));
    }

    private static void SkipGgufValue(BinaryReader reader, uint type)
    {
        // GGUF metadata value types (llama.cpp gguf_type enum):
        // 0 UINT8, 1 INT8, 2 UINT16, 3 INT16, 4 UINT32, 5 INT32, 6 FLOAT32,
        // 7 BOOL, 8 STRING, 9 ARRAY, 10 UINT64, 11 INT64, 12 FLOAT64.
        switch (type)
        {
            case 0: reader.ReadByte(); break;
            case 1: reader.ReadSByte(); break;
            case 2: reader.ReadUInt16(); break;
            case 3: reader.ReadInt16(); break;
            case 4: reader.ReadUInt32(); break;
            case 5: reader.ReadInt32(); break;
            case 6: reader.ReadSingle(); break;
            case 7: reader.ReadBoolean(); break;
            case 8: ReadGgufString(reader); break;
            case 9: // ARRAY: element type + count + values
                var elementType = reader.ReadUInt32();
                var count = reader.ReadUInt64();
                for (ulong j = 0; j < count; j++)
                {
                    SkipGgufValue(reader, elementType);
                }

                break;
            case 10: reader.ReadUInt64(); break;
            case 11: reader.ReadInt64(); break;
            case 12: reader.ReadDouble(); break;
            default:
                throw new InvalidDataException($"Unsupported GGUF metadata type {type}.");
        }
    }

    private static void CopyProvisionedExtensions(InfrastructureOptions options)
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        var source = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ai-raccoon", "extensions", rid);
        if (!Directory.Exists(source))
        {
            throw new InvalidOperationException(
                $"No native extensions provisioned for {rid}; run the server once to provision them.");
        }

        var target = Path.Combine(options.DataRoot, "extensions", rid);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static string CreateTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-raccoon-bench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
