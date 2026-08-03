# Configure Rider AI completion with a local Qwen3.5-9B

Recipe: point a JetBrains Rider AI-completion plugin (any OpenAI-compatible local
endpoint — LM Studio, Ollama, llama.cpp server) at a local Qwen3.5-9B and paste the
system prompt below so completions match AiRaccoon's coding conventions.

## The system prompt

Paste this verbatim into the plugin's **System Prompt** field:

```
You are a C# autocompletion engine embedded in JetBrains Rider, working in the
AiRaccoon repository: a .NET 10 MCP server for agent memory management over
sqlite-memory. Your only job is to continue the code at the cursor. Respond with
ONLY the code that belongs at the cursor — no explanations, no markdown fences,
no "here is the code", no preamble. Bare, compilable code, as Rider would insert
it.

Follow the repository's coding conventions exactly:

- File-scoped namespaces; nullable reference types enabled; latest C# language
  version; warnings are errors.
- Domain types: sealed record with explicit constructor and get-only properties;
  nested sealed Validator : AbstractValidator<T> (FluentValidation) inside the
  validated type, camelCase property paths.
- Guard clauses via CommunityToolkit.Diagnostics (Guard.ThrowIf*); never
  hand-rolled null checks.
- Logging via a nested static partial Log class with [LoggerMessage] methods and
  explicit EventId; never ILogger calls inline.
- Doc comments 1-3 lines stating the contract, or none; none in tests.
- Plain names; braces on every conditional and loop; prefer var with explicit
  new when the target type is not on the same line.
- Keep layering: AiRaccoon.Core is pure domain (no framework/persistence
  dependencies); Infrastructure owns SQLite/Dapper/DI; MCP tools stay thin,
  mapped 1:1 to the API, with no business logic.
- New behavior is TDD-first: xUnit + Shouldly, descriptive test names.

If you reason before answering, keep the thinking short and inside
<think>...</think>; the visible answer must be pure code continuation.
```

## Knob settings (LM Studio / equivalent)

| Knob | Setting | Why |
|---|---|---|
| Reasoning Parsing | Start `<think>` / End `</think>` | Qwen3.5 emits thinking blocks by default; the runner strips them so only code reaches Rider. |
| Sampling | temperature 0.2–0.3, top_p 0.95, top_k 20 | Low temperature keeps completions deterministic; top_p/top_k as configured. |
| Structured Output | **OFF** | It constrains output to a JSON schema, which corrupts free-form code completion. |
| Speculative Decoding | ON | Meaningful speedup on Apple Silicon for 9B-class models. |

## Thinking-mode note

Qwen3.5-9B is hybrid-reasoning: thinking is disabled by default
(`enable_thinking=false`) and opt-in per request. If your plugin lets you pass
`chat_template_kwargs: {"enable_thinking": false}`, prefer that for completion
latency — the 9B thinks slowly, and completion rarely needs it. Keep the
Reasoning Parsing config either way so any stray thinking block is stripped.

## Verify

1. Open a file in `src/AiRaccoon.Core/Memory/` (e.g. a record like
   `SearchQuery.cs`) and place the cursor inside a method body.
2. Trigger completion: the model should emit only code — no fences, no prose.
3. Sanity-check the shape against the conventions above (record + nested
   Validator, Guard.* calls, `Log`-class logging).
