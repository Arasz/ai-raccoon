# 0031. Polly Resilience Pipelines with Exponential Backoff and Decorrelated Jitter

Date: 2026-08-13

## Status
Accepted

## Context
AiRaccoon performs outbound HTTP and network operations across two distinct domains:
1. **Local Loopback Operations (`http://127.0.0.1:<port>`)**: `ServerProbe` (checking server health), `ServerRestart` (requesting graceful shutdown/drain), and `ObservabilityRunner` (polling metrics). During background server startup and restart cycles, local sockets can briefly throw `SocketException` / `HttpRequestException: Connection refused` or transient 500/503 errors while ASP.NET Core initializes.
2. **Remote Operations**: `BundledModel` / `AssetDownloader` fetching ONNX models and vocabulary assets over remote HTTP endpoints. Remote downloads are subject to transient network drops, rate limits (HTTP 429), and gateway timeouts (500, 502, 503, 504).

Prior implementation relied on manual `for` loops with ad hoc `try/catch` blocks or lacked retry policies entirely. Concurrent probes and clients retrying without jitter could cause "thundering herd" contention on loopback sockets or remote APIs.

## Decision
We adopt **Polly v8** (`Polly.Core` + `Microsoft.Extensions.Http.Resilience`) to standardize all HTTP resilience policies across the codebase:

1. **Exponential Backoff with Decorrelated Random Jitter**: All policies use `DelayBackoffType.Exponential` paired with `UseJitter = true` (or custom random drift) to spread retry attempts and prevent thundering herd collisions.
2. **Domain-Specific Policy Profiles**:
   - **`ServerProbe`**: Fast loopback retry pipeline (3 attempts, 25ms initial delay, exponential backoff with random jitter, 1.5s overall timeout). Replaces manual `for` loops in `ServerProbe`.
   - **`ServerRestart` / `ObservabilityRunner`**: Fast loopback retry pipeline (3 attempts, 50ms initial delay, exponential backoff with random jitter).
   - **`AssetDownloader`**: Heavy remote retry pipeline (5 attempts, 500ms initial delay, exponential backoff with decorrelated jitter, handling HTTP 429 `Retry-After` and 5xx status codes).
3. **Clean Architecture & Injectable Pipelines**:
   - Resilience pipeline configurations are centralized in `ResiliencePipelineFactory` (`AiRaccoon.Core` / `AiRaccoon.Infrastructure`).
   - Integrated into `IHttpClientFactory` registration via `Microsoft.Extensions.Http.Resilience`.
   - `ServerProbe` accepts an optional `ResiliencePipeline` or `IResiliencePipelineProvider` for TDD mockability.

## Consequences
- **Positive**: Eliminates hand-rolled retry loops with a battle-tested, zero-allocation Polly v8 engine.
- **Positive**: Random jitter prevents concurrent clients/probes from hammering local loopback sockets or remote APIs simultaneously.
- **Positive**: Standardized, central policy definitions with full OpenTelemetry and ILogger integration.
- **Negative**: Adds a dependency on `Polly.Core` and `Microsoft.Extensions.Http.Resilience`.
