# how-to/

Task-oriented recipes: the reader has a goal (configure server, switch embeddings, export telemetry) and follows the steps. Filenames start with an imperative verb.

## Contents

- [Change the embed drain's rows-per-run](change-the-embed-rows-per-run.md) — set the number of pending rows one drain pass takes, with the settings key it writes, the range it accepts, and what a bigger pass costs in memory.
- [Configure and run the AiRaccoon server](configure-ai-raccoon-server.md) — launch flags, environment variables, SQLite passphrase encryption (including keying the bank from 1Password), serve mode lifecycle, idle watchdog, and zero-downtime updates with `serve --restart`.
- [Configure embedding engines](configure-embedding-engines.md): switch between the bundled local ONNX model (`granite-embedding-small-english-r2`) and remote OpenAI-compatible backends (Ollama, LM Studio, OpenAI) with `ai-raccoon model`.
- [Configure Rider AI completion with a local Qwen3.5-9B](configure-rider-local-autocompletion.md) — point a Rider AI-completion plugin at a local Qwen3.5-9B endpoint and paste a system prompt that matches the repo's C# conventions.
- [Monitor and export server telemetry](monitor-and-export-telemetry.md) — live process discovery, `dotnet-counters`/`dotnet-trace` integration, and OTLP metrics/traces export.
- [Read back performance metrics](read-performance-metrics.md) — ask the running server how it is performing with `get_engine_statistics`, no OTLP collector required.
- [Rekey an encrypted bank](rekey-an-encrypted-bank.md) — move a Bitwarden/SSH-keyed bank from the pre-ADR-0012 key derivation to the current HKDF one with `ai-raccoon encryption migrate`.
- [Repair fragmented project ids](repair-fragmented-project-ids.md): fold project ids that name the same project into one canonical id with `ai-raccoon repair project-ids`, using an alias-map template the tool writes for you.
- [Run the Python scripts](run-the-python-scripts.md) — set up `uv`, install the declared dependencies, and run `scripts/` tooling and its test suite from a bare checkout.
- [Search the code corpus](search-the-code-corpus.md) — search project source code via `query kind=code`, read chunks with `get_entry`, and set up the code embedding engine.
