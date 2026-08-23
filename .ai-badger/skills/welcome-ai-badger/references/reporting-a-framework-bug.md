# Reporting a framework bug

Shared by `welcome-ai-badger`, `den-refresh` and `feed-badger`: the procedure is identical in all three, so it lives here once. Reached after every applicable recovery fix has been tried and the step still fails.

## The rule that matters

**Never create an issue without explicit user approval.** The user may prefer to debug locally, may not want the project details published, or may want to file it themselves.

## Procedure

1. **Ask for permission.** "Recovery failed. Should I create a GitHub issue in
   `Arasz/ai-badger` with the error details?"

2. **Gate on `gh`.** Only offer if `command -v gh` succeeds *and* `gh auth status` returns 0. If `gh` is unavailable, print the error details and suggest filing the issue manually — do not offer a command that cannot run.

3. **Create the issue** (only after approval):

   ```bash
   gh issue create \
     --repo Arasz/ai-badger \
     --title "bug: <skill> failed — <error summary>" \
     --body "<structured body>" \
     --label "bug,triage"
   ```

4. **Body structure:**

   ````markdown
   ## <skill> failure

   **Failed step:** <which script / phase>
   **Framework version:** <from VERSION>
   **OS / Python:** <os> / <python version>
   **gh version:** <gh --version>

   ### Error output
   ```json
   <full JSON error from the script>
   ```

   ### Recovery attempts
   1. <what was tried>
   2. <what was tried>

   ### Project config (sanitized)
   ```json
   <config.json with any secrets removed>
   ```
   ````

5. **Sanitize before sending.** Strip tokens, connection strings and anything else that looks like a credential from every block above. An issue is public.
