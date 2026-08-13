# Simulated Human Grading Workflow

To quickly evaluate retrieval quality before a full automated grading pipeline is ready, use a simulated human grading workflow via `delegate_task`.

## Workflow Steps

1. **Extract Data:** Query the `search_quality` table (or similar) for a batch of ungraded search results and extract them to a JSON file (e.g., via a Python script).
2. **Dispatch Expert Subagent:** Use `delegate_task` to dispatch a subagent to evaluate the batch.
    - Provide the exact scoring rubric (e.g., 1-5 scale with definitions).
    - Assign the persona of a domain expert (e.g., "RAG and memory retrieval expert").
    - Pass the extracted JSON file path.
    - Define a strict JSON `output_schema` requiring correlation IDs, integer scores, and explanations to ensure parser-friendly output.
3. **Apply Results:** Read the subagent's output JSON and use a Python script to `UPDATE` the database, applying the simulated grades and explanations.

This acts as a "Mixture of Experts" (MoE) evaluation pass, establishing a reliable baseline to compare against automated models or new embedding strategies.
