using System.Security.Cryptography;
using System.Text;
using AiRaccoon.Infrastructure.Embedding;

namespace AiRaccoon.Tests.Unit.Embedding.Download;

/// <summary>A small fake HF repo shared by the service and CLI mocked-download tests: a generated
/// ONNX with external_data plus a sentencepiece (bge-m3-style) or bert (root-layout) tokenizer.</summary>
    internal sealed class FakeRepo
    {
        public required string RepoId { get; init; }

        public required string Revision { get; init; }

        public required byte[] OnnxBytes { get; init; }

        public required byte[] DataBytes { get; init; }

        public required byte[] SpmBytes { get; init; }

        public required string OnnxSha { get; init; }

        public required string DataSha { get; init; }

        public required string SpmSha { get; init; }

        public required string ConfigJson { get; init; }

        public required string TokenizerConfigJson { get; init; }

        public static FakeRepo BgeM3(FakeHfServer server, bool corruptOnnx = false, bool corruptSpm = false,
            long? declaredDataSize = null, bool withSentenceTransformersLayout = false)
        {
            var onnx = TestOnnx.MinimalModelWithExternalData("model.onnx_data");
            var data = "external-weights-bytes"u8.ToArray();
            var spm = "sentencepiece-model-bytes"u8.ToArray();
            var repo = new FakeRepo
            {
                RepoId = "test/fake-model",
                Revision = "main",
                OnnxBytes = onnx,
                DataBytes = data,
                SpmBytes = spm,
                OnnxSha = Sha(onnx),
                DataSha = Sha(data),
                SpmSha = Sha(spm),
                ConfigJson = """{"model_type": "xlm-roberta", "hidden_size": 1024, "max_position_embeddings": 8194, "vocab_size": 250002}""",
                TokenizerConfigJson =
                    """
                    {
                      "bos_token": "<s>", "eos_token": "</s>", "unk_token": "<unk>", "pad_token": "<pad>",
                      "add_bos_token": true, "add_eos_token": true,
                      "added_tokens_decoder": {
                        "0": { "content": "<s>", "special": true },
                        "1": { "content": "<pad>", "special": true },
                        "2": { "content": "</s>", "special": true },
                        "3": { "content": "<unk>", "special": true }
                      }
                    }
                    """
            };
            var stEntries = withSentenceTransformersLayout
                ? ",\n  { \"path\": \"1_Pooling/config.json\", \"type\": \"file\", \"size\": 100, \"lfs\": null },\n  { \"path\": \"modules.json\", \"type\": \"file\", \"size\": 100, \"lfs\": null }"
                : string.Empty;
            server.Tree(repo.RepoId, repo.Revision,
                $$"""
                [
                  { "path": "onnx/model.onnx", "type": "file", "size": {{onnx.Length}}, "lfs": { "oid": "{{repo.OnnxSha}}" } },
                  { "path": "onnx/model.onnx_data", "type": "file", "size": {{declaredDataSize ?? data.Length}}, "lfs": { "oid": "{{repo.DataSha}}" } },
                  { "path": "onnx/sentencepiece.bpe.model", "type": "file", "size": {{spm.Length}}, "lfs": { "oid": "{{repo.SpmSha}}" } },
                  { "path": "onnx/config.json", "type": "file", "size": 100, "lfs": null },
                  { "path": "onnx/tokenizer_config.json", "type": "file", "size": 100, "lfs": null }{{stEntries}}
                ]
                """);
            server.Resolve(repo.RepoId, "onnx/model.onnx", corruptOnnx ? "corrupted-bytes"u8.ToArray() : onnx);
            server.Resolve(repo.RepoId, "onnx/model.onnx_data", data);
            server.Resolve(repo.RepoId, "onnx/sentencepiece.bpe.model", corruptSpm ? "corrupted-spm"u8.ToArray() : spm);
            server.Resolve(repo.RepoId, "onnx/config.json", Encoding.UTF8.GetBytes(repo.ConfigJson));
            server.Resolve(repo.RepoId, "onnx/tokenizer_config.json", Encoding.UTF8.GetBytes(repo.TokenizerConfigJson));
            if (withSentenceTransformersLayout)
            {
                server.Resolve(repo.RepoId, "1_Pooling/config.json",
                    Encoding.UTF8.GetBytes("""{"word_embedding_dimension": 1024, "pooling_mode_cls_token": true, "pooling_mode_mean_tokens": false}"""));
                server.Resolve(repo.RepoId, "modules.json",
                    Encoding.UTF8.GetBytes("""[{"idx": 0, "name": "1_Pooling", "path": "", "type": "sentence_transformers.models.Pooling"}, {"idx": 1, "name": "2_Normalize", "path": "", "type": "sentence_transformers.models.Normalize"}]"""));
            }

            return repo;
        }

        public static FakeRepo BertRoot(FakeHfServer server)
        {
            var onnx = TestOnnx.MinimalModelWithExternalData("model.onnx_data");
            var data = "external-weights-bytes"u8.ToArray();
            var vocab = "bert-vocab-bytes"u8.ToArray();
            var repo = new FakeRepo
            {
                RepoId = "test/bert-root",
                Revision = "main",
                OnnxBytes = onnx,
                DataBytes = data,
                SpmBytes = vocab,
                OnnxSha = Sha(onnx),
                DataSha = Sha(data),
                SpmSha = Sha(vocab),
                ConfigJson = """{"model_type": "bert", "hidden_size": 384, "max_position_embeddings": 258, "vocab_size": 30522}""",
                TokenizerConfigJson =
                    """
                    {
                      "bos_token": "[CLS]", "eos_token": "[SEP]", "unk_token": "[UNK]", "pad_token": "[PAD]",
                      "added_tokens_decoder": {
                        "0": { "content": "[PAD]", "special": true },
                        "101": { "content": "[CLS]", "special": true },
                        "102": { "content": "[SEP]", "special": true },
                        "100": { "content": "[UNK]", "special": true }
                      }
                    }
                    """
            };
            server.Tree(repo.RepoId, repo.Revision,
                $$"""
                [
                  { "path": "model.onnx", "type": "file", "size": {{onnx.Length}}, "lfs": { "oid": "{{repo.OnnxSha}}" } },
                  { "path": "model.onnx_data", "type": "file", "size": {{data.Length}}, "lfs": { "oid": "{{repo.DataSha}}" } },
                  { "path": "vocab.txt", "type": "file", "size": {{vocab.Length}}, "lfs": { "oid": "{{repo.SpmSha}}" } },
                  { "path": "config.json", "type": "file", "size": 100, "lfs": null },
                  { "path": "tokenizer_config.json", "type": "file", "size": 100, "lfs": null }
                ]
                """);
            server.Resolve(repo.RepoId, "model.onnx", onnx);
            server.Resolve(repo.RepoId, "model.onnx_data", data);
            server.Resolve(repo.RepoId, "vocab.txt", vocab);
            server.Resolve(repo.RepoId, "config.json", Encoding.UTF8.GetBytes(repo.ConfigJson));
            server.Resolve(repo.RepoId, "tokenizer_config.json", Encoding.UTF8.GetBytes(repo.TokenizerConfigJson));
            return repo;
        }

        /// <summary>
        ///     The faxenoff/code-daemon-embed-v1 shape (issue #417): a sentencepiece repo whose
        ///     tokenizer_config.json ships no added_tokens_decoder. Uses the REAL bundled
        ///     code-sentencepiece.bpe.model bytes (not a fake placeholder) — the fix derives
        ///     special-token ids from this file's own piece table, so the fixture must be a piece
        ///     table an actual sentencepiece parse can read.
        /// </summary>
        public static FakeRepo CodeDaemon(FakeHfServer server, bool declareUnresolvablePiece = false)
        {
            var onnx = TestOnnx.MinimalModelWithExternalData("model.onnx_data");
            var data = "external-weights-bytes"u8.ToArray();
            var spm = File.ReadAllBytes(CodeTokenizer.ResolveModelPath());
            var padToken = declareUnresolvablePiece ? "<not-a-real-piece>" : "<pad>";
            var repo = new FakeRepo
            {
                RepoId = "faxenoff/code-daemon-embed-v1",
                Revision = "main",
                OnnxBytes = onnx,
                DataBytes = data,
                SpmBytes = spm,
                OnnxSha = Sha(onnx),
                DataSha = Sha(data),
                SpmSha = Sha(spm),
                ConfigJson = """{"model_type": "roberta", "hidden_size": 768, "max_position_embeddings": 130, "vocab_size": 22739}""",
                TokenizerConfigJson =
                    $$"""
                    {
                      "model_max_length": 128,
                      "bos_token": "<s>", "eos_token": "</s>", "unk_token": "<unk>", "pad_token": "{{padToken}}"
                    }
                    """
            };
            server.Tree(repo.RepoId, repo.Revision,
                $$"""
                [
                  { "path": "model.onnx", "type": "file", "size": {{onnx.Length}}, "lfs": { "oid": "{{repo.OnnxSha}}" } },
                  { "path": "model.onnx_data", "type": "file", "size": {{data.Length}}, "lfs": { "oid": "{{repo.DataSha}}" } },
                  { "path": "sentencepiece.bpe.model", "type": "file", "size": {{spm.Length}}, "lfs": { "oid": "{{repo.SpmSha}}" } },
                  { "path": "config.json", "type": "file", "size": 100, "lfs": null },
                  { "path": "tokenizer_config.json", "type": "file", "size": 100, "lfs": null }
                ]
                """);
            server.Resolve(repo.RepoId, "model.onnx", onnx);
            server.Resolve(repo.RepoId, "model.onnx_data", data);
            server.Resolve(repo.RepoId, "sentencepiece.bpe.model", spm);
            server.Resolve(repo.RepoId, "config.json", Encoding.UTF8.GetBytes(repo.ConfigJson));
            server.Resolve(repo.RepoId, "tokenizer_config.json", Encoding.UTF8.GetBytes(repo.TokenizerConfigJson));
            return repo;
        }

        private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
