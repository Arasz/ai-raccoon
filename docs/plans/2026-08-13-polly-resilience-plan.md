# Polly Resilience Architecture & Implementation Plan

**Author**: AiRaccoon Core Engineering  
**Date**: 2026-08-13  
**Status**: Plan Under Review  

---

## 1. Context & Objectives

Network calls in AiRaccoon fall into two distinct operational categories:
1. **Local Loopback Operations (`http://127.0.0.1:<port>`)**:
   - `ServerProbe`: Probing if an MCP server is listening and healthy during startup or background server discovery.
   - `ServerRestart`: Sending drain/shutdown requests to a running background instance on port 7721.
   - `ObservabilityRunner`: Polling metrics and OTLP counters from a live server instance.
   - *Failure Modes*: `SocketException` / `HttpRequestException` (Connection Refused) during process startup/shutdown windows, transient 500/503 errors during initialization.

2. **Remote Outbound Operations**:
   - `BundledModel` / `AssetDownloader`: Downloading ONNX embedding model files (`model_qint8_arm64.onnx`, `vocab.txt`) from remote endpoints (HuggingFace, GitHub Releases, S3).
   - *Failure Modes*: Transient network drops, rate limits (HTTP 429), gateway timeouts (502, 503, 504), request timeout (408).

Currently, retries are either hand-rolled (`for` loops) or non-existent. We need to integrate **Polly v8** (`Polly.Core` + `Microsoft.Extensions.Http.Resilience`) to establish unified, production-grade resilience pipelines featuring **exponential backoff with decorrelated random jitter/drift**.

---

## 2. Analyzed Call Sites & Policy Specifications

| Client / Component | Primary Use Case | Target Failure Types | Max Attempts | Initial Delay | Backoff Algorithm | Jitter / Drift | Timeout |
| :--- | :--- | :--- | :---: | :---: | :--- | :---: | :---: |
| **`ServerProbe`** | Local server probe | `HttpRequestException`, `SocketException`, 5xx | 3 | 25ms | Exponential ($25ms, 50ms, 100ms$) | Random Jitter ($\pm 25\%$) | 1.5s total |
| **`ServerRestart`** | Local shutdown / drain | `HttpRequestException`, 5xx | 3 | 50ms | Exponential ($50ms, 100ms, 200ms$) | Random Jitter ($\pm 25\%$) | 2.0s total |
| **`ObservabilityRunner`** | Local metrics polling | `HttpRequestException`, 5xx | 3 | 50ms | Exponential ($50ms, 100ms, 200ms$) | Random Jitter ($\pm 25\%$) | 2.0s total |
| **`AssetDownloader`** | Remote model download | `HttpRequestException`, 408, 429, 5xx | 5 | 500ms | Exponential ($500ms, 1s, 2s, 4s, 8s$) | Decorrelated Jitter ($\pm 35\%$) | 60s total |

---

## 3. Architecture & Class Design

### A. Core Abstractions (`AiRaccoon.Core` / `AiRaccoon.Infrastructure`)
1. **`ResiliencePipelineFactory`**:
   - Provides reusable, thread-safe `ResiliencePipeline` instances configured via `ResiliencePipelineBuilder`.
   - Supports exponential backoff using `DelayBackoffType.Exponential` with `UseJitter = true`.
   - Injectable interface `IResiliencePipelineProvider` for clean testability and mocking in TDD.

2. **`ResilienceRegistrations` (`AiRaccoon.Setup.Extensions`)**:
   - Configures `IHttpClientFactory` clients (`nameof(ServerProbe)`, `nameof(ServerRestart)`, `nameof(ObservabilityRunner)`, `nameof(AssetDownloader)`) using `AddResilienceHandler` or Polly pipelines.

3. **`ServerProbe` Refactoring**:
   - Refactor `ServerProbe` to use `ResiliencePipeline` / `IHttpClientFactory` with Polly, eliminating hand-rolled `for` loops.

---

## 4. Implementation Workflow & Gates

### Phase 1: Architecture & Plan Review (This Step)
- Architect review of proposed policies and pipeline configurations.

### Phase 2: ADR (Architectural Decision Record)
- Create `docs/adr/0019-polly-resilience-pipelines.md` documenting the adoption of Polly v8, policy choices per client, and zero-allocation performance considerations.

### Phase 3: Test-Driven Development (TDD)
- **Failing Red Tests**:
  - `ServerProbeResilienceTests.cs`: Test `ServerProbe` retries on `SocketException` / `HttpRequestException` and succeeds on 2nd or 3rd attempt after backoff.
  - `ResiliencePipelineFactoryTests.cs`: Verify exponential backoff delay calculation and random jitter variability.
  - `AssetDownloaderResilienceTests.cs`: Verify 429/503 retries with backoff and jitter.
- **Green Implementation**: Implement `ResiliencePipelineFactory`, `ServerProbe` refactoring, and `AssetDownloader` resilience.
- **Refactor**: Clean up and optimize logging/telemetry integration.

### Phase 4: Quality Gate & Verification
- Execute `dotnet test` across unit and integration test suites.
- Verify zero regressions and check code coverage.
- Perform a manual test with `ai-raccoon serve --restart` and server probe execution.

### Phase 5: Documentation Gap Audit
- Update `docs/reference/agent-memory-server.md` and `CLAUDE.md` to record the Polly resilience implementation.

---

## 5. Acceptance Criteria

- [ ] All HTTP client configurations in `NodeRegistration.cs`, `ProxyRegistrations.cs`, and `AssetDownloader.cs` use Polly resilience.
- [ ] Exponential backoff with random jitter is verified via unit tests.
- [ ] `ServerProbe` uses Polly pipeline instead of manual `for` loops.
- [ ] ADR 0019 is written and committed in `docs/adr/0019-polly-resilience-pipelines.md`.
- [ ] All tests pass in `dotnet test`.
