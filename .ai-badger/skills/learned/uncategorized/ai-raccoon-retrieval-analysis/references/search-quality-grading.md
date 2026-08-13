# Memory Search Quality Grading

Retrieval quality is tracked in `~/.ai-raccoon/memory.db` (`search_quality` table). Grades range from 1 to 5, evaluated based on a strict rubric (1=Irrelevant noise, 5=Decisive hit).

## Evaluation Methods

1. **Automated (Prometheus):** Use `python3 ./scripts/prometheus_grade.py --limit <N>`. Requires LM Studio running locally (port 1234) with the `prometheus-7b` or `m-prometheus-14b` model loaded. The script fetches snippets from the
   `entries` table or falls back to disk.
2. **Subagent Simulated Grading:** Export ungraded queries (`usefulness_grade IS NULL`) to JSON along with their `results_summary`. Delegate a `delegate_task` subagent as a "RAG expert" to read the JSON and apply the 1-5 rubric, outputting
   grades via a strictly defined JSON schema.

## Noise Identification

If average search quality grades are low (e.g., < 3.0), manually inspect the queries in `search_quality`. A frequent cause of poor retrieval is **background process completion logs** (e.g.,
`[IMPORTANT: Background process proc_... completed normally...]`) and transient terminal output being indexed into the database. These false entries pollute the vector space. When this noise is identified, architectural filtering solutions
such as ingestion filtering, search-time filtering, or degradation sweeps (TTL) should be proposed and implemented to preserve the integrity of the memory bank.
