# Repo-wide identifier rename (project/product name change)

Verified 2026-08 on a .NET repo renamed `AiRaccon` → `AiRaccoon` (a typo fix spanning namespaces, directories, project files, env vars, data roots, docs). ~330 content occurrences across ~90 files + 11 named files/dirs. Build stayed green
and all 168 tests (including 12 real-extension integration tests) passed after the rename. These are the pitfalls that cost real iteration time.

## Workflow that worked

1. **Enumerate first, edit second** (refactor-safely discipline). Count every variant of the token: PascalCase (`AiRaccon`), odd-casing variants (`AIRaccon` — a directory whose on-disk case differs from its git-tracked case), lowercase
   (`ai-raccon`), uppercase prefix (`AIRACCON_`). Each variant is a separate replacement, and each appears in different file kinds (namespaces in `.cs`, paths in `.slnx`/`.csproj`, URLs/README in
   `.md`, env vars in `.cs`+`.json`+`.md`).
2. **`git mv` named files/dirs first** (preserves history — git detects renames on commit).
    - `git mv` of a **directory** renames the directory but NOT the files inside it. The
      `.csproj`/`.slnx` inside keep their old names; the solution file references the new names → `MSB3202: The project file "...Foo.csproj" was not found`. After moving directories, `git mv` each project file inside to match.
    - On a case-insensitive filesystem (macOS default), the on-disk name can differ in case from the git-indexed name (`tests/AIRaccon.Tests` on disk vs `tests/AiRaccon.Tests`
      in the index). `git mv` fails with "source directory is empty" if you pass the on-disk name. Use the **git-tracked** name (`git ls-files` shows the real index path).
3. **Content replacement in the right ORDER** — this is the subtle one. Replace in decreasing specificity so no pass re-replaces the output of an earlier pass:
    1. uppercase env-var prefix (`AIRACCON_` → `AIRACCOON_`)
    2. odd-casing variant (`AIRaccon` → `AiRaccoon` — folds the weird dir casing into the canonical form)
    3. canonical PascalCase (`AiRaccon` → `AiRaccoon`)
    4. lowercase (`ai-raccon` → `ai-raccoon`)
    5. any remaining bare `raccon`/`Raccon` (test method names, comments)
       A single pass over `git grep -l`'d files with `str.replace` per pair is fine; just run the pairs in this order in one script.
4. **Rename runtime data roots too.** If the product name appears in user data paths (`~/.ai-raccon` → `~/.ai-raccoon`), rename the actual directory. Integration tests that provision/copy real artifacts from the data root will otherwise
   **silently skip**
   (the test count shifts from e.g. 2 skips to 14 skips — a signal that something that used to run no longer does). `mv ~/.ai-raccon ~/.ai-raccoon` and re-run the integration filter to confirm the skip count returns to baseline.
5. **Bracket with tests**: full `dotnet build` (0 warnings) + `dotnet test` before and after, plus the integration filter specifically (catches the data-root skip regression).
6. **Verify zero stragglers with `git grep` AFTER the commit**, not just in the working tree. `git grep -l` against HEAD catches old tokens in committed test-method names that a working-tree grep may have missed (test method names like
   `..._RacconMetaDb` survive content replacement if the file wasn't in the `git grep -l` list the first time). Search for the bare token `[Rr]accon` too — test names often embed it without a prefix.

## Pitfall table

| Pitfall                                  | Symptom                                                                                                                                    | Fix                                                                                     |
|------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------|
| `git mv` dir doesn't rename files inside | `MSB3202 project file not found` from the slnx                                                                                             | `git mv` each `.csproj` inside the moved dirs                                           |
| Case-insensitive FS masks dir case       | `git mv` says "source directory is empty"                                                                                                  | Use the git-indexed name from `git ls-files`                                            |
| Replacement in wrong order               | `AiRaccon` → `AiRaccoon` then lowercase pass turns `ai-raccoon` back into `ai-raccoon` (double-o collision) or `AIRACCON_` env vars missed | Order: uppercase prefix → odd casing → PascalCase → lowercase → bare                    |
| Data root not renamed                    | Integration tests silently skip (skip count jumps)                                                                                         | `mv` the data root dir; re-run integration filter                                       |
| Verifying before commit                  | Stragglers in committed test method names                                                                                                  | `git grep -l` against HEAD after commit                                                 |
| Renaming repo root / parent dirs         | Rider/IDE open at old path                                                                                                                 | Move them too if the user says "rename everything"; verify git intact + full gate after |

## Reference commands

```bash
# enumerate variants before editing
for v in "AiRaccon" "AIRaccon" "ai-raccon" "AIRACCON" "Raccon" "raccon"; do
  echo "$v: $(git grep -l "$v" -- '*.cs' '*.csproj' '*.slnx' '*.json' '*.md' '*.props' '*.feature' | wc -l)"
done

# named files/dirs containing the token
find . -name "*[Rr]accon*" -not -path "./.git/*" -not -path "*/bin/*" -not -path "*/obj/*"

# zero-straggler gate (post-commit)
git grep -lE 'AiRaccon|AIRaccon|AIRACCON|ai-raccon|[Rr]accon' -- '*.cs' '*.csproj' '*.slnx' '*.json' '*.md' '*.props' '*.feature' | grep -v raccoon
```
