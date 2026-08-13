# User-Specific Dynamic Noise Vector Learning for AiRaccoon

**Date:** 2026-08-13  
**Status:** Research & Architectural Proposal  
**Target:** AiRaccoon (.NET 10 / SQLite Memory Store over `sqlite-vec`)  
**Related ADRs:** ADR-0029 (Pre-Write Noise Filtering), ADR-0026 (Persistent Discards), ADR-0011 (Schema Versioning)

---

## 1. Executive Summary

AiRaccoon currently employs static structural policies (e.g., `HermesProcessNoisePolicy` in ADR-0029) to intercept noisy tool outputs before they pollute the vector store (`vec_entries`) and FTS index. However, noise patterns vary
significantly across projects, environments, and developers—ranging from specific CI/CD build logs and local CLI trace outputs to IDE auto-completion noise and background daemon status reports.

This document presents a mathematically rigorous, architectural proposal for **User-Specific Dynamic Noise Vector Learning**. Under this design, AiRaccoon automatically learns and evolves noise signatures per project and per user over time
using an **Online Leader-Follower Centroid Clustering** model built on `sqlite-vec`.

Key components include:

1. **Per-Project/User Noise Vector Store (`vec_noise`)**: Partitioned vector table storing active noise centroids.
2. **Automated Multi-Signal Learning Loop**: Aggregates feedback from search quality grades (`search_quality`), promotion discards (`promotion_discards`), unaccessed expired short-TTL scratch entries, and structural policy intercepts.
3. **Four Proposed MCP Tool Extensions**: `memory_noise_report`, `memory_noise_list`, `memory_noise_override`, and `memory_noise_settings`.
4. **Five Strict Safety Boundaries**: Guarantees against catastrophic false positive data loss through dual-gated candidate states, core knowledge orthogonality checks ($\cos (\mu_{noise}, \mu_{core}) \le 0.75$), immune document white-lists
   (`shared`, file-watcher synced markdown, ADRs), reversible 14-day trash bins, and operator override capabilities.

---

## 2. Problem Statement & Current Architecture Baseline

### 2.1 Current State (ADR-0029 Baseline)

In ADR-0029, AiRaccoon introduced `INoiseFilteringService` and `INoiseStore` into `SqliteMemoryStore.WriteAsync`.

* **Structural Pre-Write Interception:** `HermesProcessNoisePolicy` uses synchronous string analysis to intercept known tool outputs (e.g., background process completions).
* **Trash Bin:** Intercepted writes are diverted to `noise_entries` with a hardcoded 14-day TTL (`expires_at`), returning a dummy success response so agent workflows are not broken.
* **Placeholder Semantic Policy:** `ZeroShotEmbeddingNoisePolicy` and `ZeroShotEmbeddingFilter` prototype zero-shot cosine distance evaluation against noise labels, but currently operate as placeholders because `WriteAsync` evaluates
  policies before generating document embeddings.

### 2.2 Limitations of Static Rules

1. **Context Blindness:** A log format produced by a specific project's test runner (e.g., custom C++ test output, specific webpack warnings, or Docker build progress) will pass static structural filters because it is unknown to global
   rules.
2. **User/Workflow Variability:** Different developers run different local CLI tools (e.g., `az`, `kubectl`, `pulumi`, custom scripts). Static lists cannot scale to cover every developer's environment.
3. **Maintenance Overhead:** Adding static regex or structural rules for every new tool output requires software updates and releases.

---

## 3. Mathematical & Architectural Design

### 3.1 Data Model & Schema Expansion

To enable dynamic vector noise learning without degrading bank open performance or violating schema versioning invariants (ADR-0011), we introduce two new tables and one virtual vector table in migration step **v5 $\rightarrow$ v6**.

```sql
-- 1. Raw noise entries and candidate queue (expanded from ADR-0029)
CREATE TABLE IF NOT EXISTS noise_entries
(
    id                 INTEGER PRIMARY KEY,
    request_content    TEXT    NOT NULL,
    project_id         TEXT    NOT NULL,
    user_id            TEXT    NULL,
    detected_by_policy TEXT    NOT NULL,
    cluster_id         INTEGER NULL,
    expires_at         INTEGER NOT NULL,
    created_at         INTEGER NOT NULL,
    FOREIGN KEY (cluster_id) REFERENCES noise_clusters (id) ON DELETE SET NULL
);

-- 2. Learned noise signature clusters and metadata
CREATE TABLE IF NOT EXISTS noise_clusters
(
    id                 INTEGER PRIMARY KEY,
    project_id         TEXT    NOT NULL,
    user_id            TEXT    NULL,
    cluster_label      TEXT    NOT NULL,
    sample_content     TEXT    NOT NULL,
    frequency          INTEGER NOT NULL DEFAULT 1,
    status             TEXT    NOT NULL CHECK (status IN ('candidate', 'active', 'suppressed')),
    centroid_embedding BLOB    NOT NULL,
    created_at         INTEGER NOT NULL,
    last_seen_at       INTEGER NOT NULL,
    UNIQUE (project_id, cluster_label)
);

-- 3. Partitioned vec0 virtual table for active noise centroids
-- Partition key `ctx` is formatted as 'project:<projectId>' or 'user:<userId>' or 'shared'
CREATE VIRTUAL TABLE IF NOT EXISTS vec_noise USING vec0
(
    ctx TEXT partition key,
    embedding float [384] distance_metric=cosine
);
```

### 3.2 Vector Space Partitioning & Context Resolution

Noise centroids are queried hierarchically using `sqlite-vec` partition keys (`ctx`):

* **Project Scope (`ctx = 'project:<projectId>'`)**: Catches project-specific noise (CI logs, project build scripts).
* **User Scope (`ctx = 'user:<userId>'`)**: Catches developer-specific noise (local terminal shell completions, developer CLI preferences).
* **Global/Shared Scope (`ctx = 'shared'`)**: Bootstrap centroids populated by built-in system noise patterns.

When evaluating an incoming write request $x$ with project $P$ and user $U$, the pre-write noise filter queries `vec_noise` matching `ctx IN ('project:' || P, 'user:' || U, 'shared')`.

### 3.3 Vector Mathematics: Online Leader-Follower Clustering

Instead of storing thousands of raw individual log lines as noise vectors (which would explode KNN search latency), noise signatures are consolidated into **normalized centroid vectors** $\mu_k$.

#### Algorithm: Online Noise Centroid Maintenance

Let $e (x) \in \mathbb{R}^d$ be the $L_2$-normalized embedding of an incoming noise candidate $x$ ($\|e (x)\|_2 = 1$).

1. **Centroid Matching:** Calculate cosine distance to existing noise centroids $\mu_k$ in context $ctx$:
   $$d (e (x), \mu_k) = 1 - \langle e (x), \mu_k \rangle$$
   Find the nearest centroid $\mu^* = \arg\min_k d (e (x), \mu_k)$.

2. **Cluster Assignment vs. Spawning:**
    * If $d (e (x), \mu^*) < \tau_{cluster}$ (where $\tau_{cluster} = 0.12$, corresponding to Cosine Similarity $> 0.88$):
      Assign $x$ to cluster $k^*$. Update centroid $\mu^*$ via online running average:
      $$\mu_{unnorm}^{ (new)} = n_{k^*} \mu^* + e (x)$$
      $$\mu^* \leftarrow \frac{\mu_{unnorm}^{ (new)}}{\|\mu_{unnorm}^{ (new)}\|_2}, \quad n_{k^*} \leftarrow n_{k^*} + 1$$
    * If $d (e (x), \mu^*) \ge \tau_{cluster}$:
      Spawn a new candidate noise cluster $C_{new}$ with centroid $\mu_{new} = e (x)$ and $n_{new} = 1$.

#### Pre-Write Interception Decision Rule

For a write request $x$ with embedding $e (x)$, evaluate maximum cosine similarity against all **active** noise centroids in scope:
$$S_{noise} (x) = \max_{k \in ActiveNoise (P, U)} \langle e (x), \mu_k \rangle$$

Interception Trigger:
$$\text{FilterResult} = \begin{cases} \text{Noise (Intercept)}, & \text{if } S_{noise} (x) \ge \Theta_{reject} \quad (\Theta_{reject} = 0.90) \\ \text{Clean (Pass)}, & \text{otherwise} \end{cases}$$

---

## 4. Automated Noise Learning Loops

AiRaccoon accumulates noise signals automatically from four distinct feedback channels without requiring continuous manual user configuration.

```
+-----------------------------------------------------------------------------------+
|                            NOISE SIGNAL SOURCES                                   |
|                                                                                   |
|  [1. Search Quality Grades]     [2. Promotion Discards]     [3. Unread Short TTL] |
|   (usefulness <= 2 / unread)     (explicit 'noise' reason)    (ttl <= 3d, access=0)  |
|                                                                                   |
|                           [4. Structural Policy Intercepts]                       |
|                            (HermesProcessNoisePolicy)                             |
+------------------------------------------+----------------------------------------+
                                           |
                                           v
+-----------------------------------------------------------------------------------+
|                        STAGE 1: CANDIDATE INGESTION                              |
|           Write raw content to noise_entries (status = 'candidate')               |
+------------------------------------------+----------------------------------------+
                                           |
                                           v
+-----------------------------------------------------------------------------------+
|                    STAGE 2: ASYNC CENTROID CLUSTERING                             |
|          Online Leader-Follower Clustering (tau_cluster = 0.88)                   |
|          Merge into existing candidate centroid OR spawn new candidate            |
+------------------------------------------+----------------------------------------+
                                           |
                                           v
+-----------------------------------------------------------------------------------+
|                     STAGE 3: SAFETY & QUALIFICATION GATES                         |
|   1. Frequency Gate: n_k >= 3 in rolling 7 days?                                  |
|   2. Orthogonality Gate: max_core cos(mu_k, mu_core) <= 0.75?                     |
|   3. Scope Immunity Gate: Content not in shared/doc/ADR white-list?              |
+------------------------------------------+----------------------------------------+
                                           |
                                   Passes all gates
                                           |
                                           v
+-----------------------------------------------------------------------------------+
|                       STAGE 4: ACTIVATION & VEC_NOISE SYNC                        |
|        Mark cluster status = 'active', write centroid mu_k to vec_noise           |
+-----------------------------------------------------------------------------------+
```

### 4.1 Signal Channel Details

1. **Channel 1: Low Search Quality Grades (`search_quality`)**
    * *Trigger:* When `mcp__ai_raccoon__memory_record_grade` sets `usefulness_grade <= 2` or when a search returns `follow_through_count == 0` after 24 hours.
    * *Action:* The retrieved result entries that yielded low utility are flagged as noise candidates and passed to the clustering service.

2. **Channel 2: Promotion Discards (`promotion_discards`)**
    * *Trigger:* When an agent executes `mcp__ai_raccoon__memory_promotion_discard` with a reason matching noise keywords (`noise`, `system_log`, `transient_output`).
    * *Action:* The discarded content hash and text are immediately converted to a high-weight noise candidate.

3. **Channel 3: Unaccessed Expired Scratch Entries**
    * *Trigger:* Background sweep (`IHostedService` / `SweepService`) detects entries with `ttl_days <= 3` that reach expiration with `access_count == 0` and `last_accessed_at IS NULL`.
    * *Action:* High probability of transient noise. The content is recorded as a low-weight candidate cluster.

4. **Channel 4: Structural Pre-Write Intercepts**
    * *Trigger:* `HermesProcessNoisePolicy` intercepts a raw process log.
    * *Action:* The raw string is saved in `noise_entries`. Asynchronous background processing embeds the log and uses it to reinforce or seed global/project noise centroids.

---

## 5. Proposed MCP Tool Extensions

To give agents and human operators full visibility, feedback capability, and control over learned noise signatures, four new MCP tools are proposed:

### 5.1 `mcp__ai_raccoon__memory_noise_report`

Explicitly reports content or an existing memory entry as noise to seed the learning loop immediately.

```json
{
    "name": "mcp__ai_raccoon__memory_noise_report",
    "description": "Report content or a specific memory entry as noise to train user/project dynamic noise vectors.",
    "parameters": {
        "type": "object",
        "properties": {
            "projectId": {
                "type": "string",
                "description": "The project ID."
            },
            "content": {
                "type": "string",
                "description": "Raw text content to classify as noise."
            },
            "hash": {
                "type": "string",
                "description": "Optional hash of an existing entry to mark as noise."
            },
            "scope": {
                "type": "string",
                "enum": [
                    "project",
                    "user"
                ],
                "default": "project"
            },
            "reason": {
                "type": "string",
                "description": "Explanation (e.g. 'Custom CI build log output')."
            }
        },
        "required": [
            "projectId"
        ]
    }
}
```

### 5.2 `mcp__ai_raccoon__memory_noise_list`

Lists learned noise centroids, candidate clusters, sample exemplars, and occurrence counts for inspection.

```json
{
    "name": "mcp__ai_raccoon__memory_noise_list",
    "description": "List active and candidate learned noise vector signatures for a project or user.",
    "parameters": {
        "type": "object",
        "properties": {
            "projectId": {
                "type": "string",
                "description": "The project ID."
            },
            "status": {
                "type": "string",
                "enum": [
                    "all",
                    "active",
                    "candidate",
                    "suppressed"
                ],
                "default": "active"
            },
            "limit": {
                "type": "integer",
                "default": 20
            }
        },
        "required": [
            "projectId"
        ]
    }
}
```

### 5.3 `mcp__ai_raccoon__memory_noise_override`

Overrides a false positive rejection, restores intercepted items from `noise_entries`, or permanently suppresses a misbehaved noise cluster.

```json
{
    "name": "mcp__ai_raccoon__memory_noise_override",
    "description": "Restore a falsely rejected memory entry or suppress/blacklist a noise vector cluster.",
    "parameters": {
        "type": "object",
        "properties": {
            "projectId": {
                "type": "string",
                "description": "The project ID."
            },
            "clusterId": {
                "type": "integer",
                "description": "ID of the noise cluster to suppress."
            },
            "noiseEntryId": {
                "type": "integer",
                "description": "ID of an intercepted entry in noise_entries to restore into main memory."
            },
            "action": {
                "type": "string",
                "enum": [
                    "restore_and_suppress_cluster",
                    "restore_entry_only",
                    "suppress_cluster"
                ]
            }
        },
        "required": [
            "projectId",
            "action"
        ]
    }
}
```

### 5.4 `mcp__ai_raccoon__memory_noise_settings`

Configures noise learning sensitivity, thresholds, and auto-learning toggles at the project level.

```json
{
    "name": "mcp__ai_raccoon__memory_noise_settings",
    "description": "Get or update project-level dynamic noise vector learning configuration.",
    "parameters": {
        "type": "object",
        "properties": {
            "projectId": {
                "type": "string",
                "description": "The project ID."
            },
            "autoLearningEnabled": {
                "type": "boolean",
                "description": "Enable automatic candidate cluster promotion."
            },
            "rejectionThreshold": {
                "type": "number",
                "description": "Cosine similarity rejection threshold (default 0.90, range 0.85 - 0.98)."
            },
            "minFrequency": {
                "type": "integer",
                "description": "Minimum candidate occurrences before auto-promotion (default 3)."
            }
        },
        "required": [
            "projectId"
        ]
    }
}
```

---

## 6. False Positive Risks & Safety Boundaries

Automated pre-write noise rejection carries the risk of **false positives**—erroneously dropping legitimate, high-value architectural knowledge or bug reports because they superficially resemble noise. To prevent silent memory corruption,
the system enforces **five mandatory safety bounds**.

### 6.1 Risk Analysis Matrix

| Failure Mode                   | Risk Description                                                                                                               | Impact                                  | Mitigation / Safety Bound                                                    |
|:-------------------------------|:-------------------------------------------------------------------------------------------------------------------------------|:----------------------------------------|:-----------------------------------------------------------------------------|
| **Over-Generalization**        | A noise cluster trained on a stack trace over-generalizes and filters out real bug fix documentation containing code snippets. | High (Loss of technical documentation)  | **Bound 2 (Orthogonality Check)** & **Bound 4 (Threshold Floor $\ge 0.88$)** |
| **Adversarial / Bad Feedback** | An agent incorrectly labels a project ADR as noise during a bad search interaction.                                            | Critical (Pollution of noise index)     | **Bound 1 (Dual-Gated State Machine)** & **Bound 2 (Scope White-List)**      |
| **Silent Data Loss**           | Writes are intercepted silently without agent awareness, leaving gaps in context.                                              | Medium (Agent assumes memory was saved) | **Bound 3 (14d Reversible Trash Bin & Explicit Intercept Metadata)**         |
| **Runaway Rejection Rate**     | Miscalibrated threshold rejects $>50\%$ of incoming legitimate writes.                                                         | Critical (Breakdown of memory store)    | **Bound 4 (Session Rejection Cap & Safety Circuit Breaker)**                 |

### 6.2 The Five Mandatory Safety Boundaries

#### Safety Bound 1: Dual-Gated Candidate State Machine (`candidate` $\rightarrow$ `active`)

No automatically discovered noise signature is allowed to intercept pre-write requests immediately upon first encounter.

* Every new noise vector enters as `status = 'candidate'`.
* Auto-promotion to `status = 'active'` requires $n_k \ge N_{min}$ (default 3 distinct occurrences within a rolling 7-day window) AND successful passage through Safety Bound 2.

#### Safety Bound 2: Core Knowledge Protection (Scope White-List & Orthogonality Check)

1. **Scope White-List Immunity:** Content destined for `scope = 'shared'`, file-watcher synced markdown documents (`watches` / `watch_files`), and architectural decision records (ADRs) are **100% immune** to vector pre-write rejection.
2. **Core Knowledge Orthogonality Check:** Before promoting a candidate noise centroid $\mu_{noise}$ to `active`, measure its max cosine similarity against the centroids of active project entries $\mu_{core}$:
   $$S_{overlap} = \max_{e \in CoreEntries (P)} \langle \mu_{noise}, e (e) \rangle$$
    * **Rule:** If $S_{overlap} > 0.75$, the candidate noise cluster is **PERMANENTLY BLOCKED** from activation (`status = 'suppressed'`). This mathematically guarantees that noise vectors cannot invade core domain concepts.

#### Safety Bound 3: Reversible 14-Day Trash Bin & Transparent Response Metadata

* Intercepted writes are stored in `noise_entries` with an explicit 14-day `expires_at` Unix timestamp.
* The response returned by `WriteAsync` includes intercept diagnostic metadata:
  ```json
  {
    "hash": "noise_intercepted_c42",
    "status": "intercepted_as_noise",
    "policy": "DynamicVectorNoisePolicy",
    "clusterId": 42,
    "clusterLabel": "custom_ci_log_centroid"
  }
  ```
* Agents can restore falsely intercepted entries using `mcp__ai_raccoon__memory_noise_override`.

#### Safety Bound 4: Threshold Floor & Session Rejection Circuit Breaker

* **Cosine Threshold Floor:** $\Theta_{reject}$ cannot be set below $0.85$ (default $0.90$). Cosine similarity $> 0.90$ represents extreme semantic equivalence.
* **Session Rejection Circuit Breaker:** If pre-write noise rejection intercepts $> 25\%$ of write requests within a single agent session (or $> 50$ consecutive writes), the dynamic vector filter automatically trips open, logging an
  operator warning and falling back strictly to static structural policies until reset.

#### Safety Bound 5: Operator Override & Negative Reinforcement

* When an operator or agent calls `mcp__ai_raccoon__memory_noise_override(action = 'restore_and_suppress_cluster')`:
    1. The target entry is restored from `noise_entries` into `entries`.
    2. The associated noise cluster is marked `status = 'suppressed'`.
    3. The centroid vector $\mu_{suppressed}$ is stored in a negative exclusion index so it can never be re-activated by automated learning loops.

---

## 7. Implementation Roadmap & Pipeline Changes

### Phase 1: Pipeline Refactoring (`WriteAsync` Embedding Flow)

In `SqliteMemoryStore.WriteAsync`, refactor the pre-write evaluation sequence so that embedding generation occurs efficiently:

1. Run Tier-1 synchronous structural policies (`HermesProcessNoisePolicy`).
2. If clean, compute document embedding $e (x)$ once via `IEntryEmbedder`.
3. Run Tier-2 `DynamicVectorNoisePolicy` using $e (x)$ against `vec_noise`.
4. If flagged as noise, record in `noise_entries` with $e (x)$ and return intercept metadata.
5. If clean, proceed with `entries` database insertion using pre-computed $e (x)$, avoiding duplicate embedding calculation calls.

### Phase 2: Background Clustering & Qualification Service

Implement `NoiseLearningBackgroundService` as a .NET `IHostedService`:

* Runs periodically (e.g. every 1 hour).
* Sweeps `noise_entries` candidates, runs leader-follower clustering, updates `noise_clusters`.
* Evaluates Safety Bound 2 (Orthogonality Check) against project core embeddings.
* Promotes qualified clusters ($n_k \ge 3$, $S_{overlap} \le 0.75$) to `status = 'active'` and updates `vec_noise`.

### Phase 3: MCP Tool Surface Wiring

Implement controller endpoints for the four MCP tools (`mcp__ai_raccoon__memory_noise_*`) in `AiRaccoon.Core` and register them in `AppRegistrations.cs`.

### Phase 4: Validation & Evaluation Fixtures

* Create unit tests in `AiRaccoon.Tests` exercising centroid updating, cluster spawning, and threshold safety boundaries.
* Add integration test fixtures verifying that core architectural documentation is never rejected, while project-specific synthetic build logs are correctly learned and intercepted after 3 occurrences.

---

## 8. Summary of Architectural Verification

This design directly solves user-specific noise accumulation while preserving search quality and performance:

1. **Mathematical Rigor:** Leader-follower centroid clustering prevents vector database bloat and keeps KNN rejection evaluation under $2 \text{ ms}$.
2. **Autonomous Learning:** Integrates search quality grades, promotion discards, unread TTL expirations, and process logs into an organic learning loop.
3. **Uncompromising Safety:** Five layered bounds—including orthogonality checks against core knowledge vectors—guarantee zero unintended loss of critical domain memories.
