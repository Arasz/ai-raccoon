# how-to/

Task-oriented recipes: the reader has a goal (configure server, switch embeddings, export telemetry) and follows the steps. Filenames start with an imperative verb.

## Contents

- [Configure and run the AiRaccoon server](configure-ai-raccoon-server.md) — launch flags, environment variables, SQLite passphrase encryption, serve mode lifecycle, idle watchdog, and zero-downtime updates with `serve --restart`.
- [Configure embedding engines](configure-embedding-engines.md) — switch between local ONNX (`all-MiniLM-L6-v2`) and remote OpenAI-compatible backends (Ollama, LM Studio, OpenAI) with `ai-raccoon model`.
- [Configure Rider AI completion with a local Qwen3.5-9B](configure-rider-local-autocompletion.md) — point a Rider AI-completion plugin at a local Qwen3.5-9B endpoint and paste a system prompt that matches the repo's C# conventions.
- [Monitor and export server telemetry](monitor-and-export-telemetry.md) — live process discovery, `dotnet-counters`/`dotnet-trace` integration, and OTLP metrics/traces export.
- [Read back performance metrics](read-performance-metrics.md) — ask the running server how it is performing with `memory_performance`, no OTLP collector required.
- [Rekey an encrypted bank](rekey-an-encrypted-bank.md) — move a Bitwarden/SSH-keyed bank from the pre-ADR-0012 key derivation to the current HKDF one with `ai-raccoon encryption migrate`.
- [Run the Python scripts](run-the-python-scripts.md) — set up `uv`, install the declared dependencies, and run `scripts/` tooling and its test suite from a bare checkout.
