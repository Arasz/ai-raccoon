# Structure — the tree, the names, the freeze list

**A project binds its own tree.** The root defaults to `docs/`; a project that keeps documentation
elsewhere says so once, and everything below is relative to that root. Where the project has a
machine-readable canonical tree, that file is the authority and this page explains what each
directory is *for*. Where it has none, the shape below is what to create.

## The recommended tree

```
<docs>/
  README.md          the compass — a COMPLETE map of every directory below
  CHANGELOG.md       generated from the ledger; never hand-edited (omit if there is no ledger)
  tutorials/         learning-oriented, we choose the goal
  how-to/            task-oriented, the reader's goal
  reference/         information-oriented, looked up mid-task
  explanation/       understanding-oriented, why it is like this
  adr/               decisions — immutable, frozen, unchanged by any migration
  work/              dated work records: plans, designs, research, reviews, incidents, backlog
  legacy/            files awaiting re-derivation; exists only during a migration, drains to empty
  assets/            images and diagrams, no quadrant
  <frozen>/          build or runtime inputs that merely happen to be markdown — NOT documentation
  meta/              machine state: the ledger, indexes, baselines, migration state
```

Four things do not vary, whatever tree a project binds:

1. **A legal home exists before any document is written.** Content written into a directory that
   does not yet exist has to be moved later, and moving it costs the file's history.
2. **Directory READMEs are complete maps, not highlight reels.** See below.
3. **Frozen build input is never edited** — not reformatted, not given frontmatter, not moved.
4. **Everything is visible. No dot-directory ever holds prose.** ripgrep does not descend into
   hidden directories without `--hidden`, and ripgrep is what the search tool of every agent
   working here is built on — a hidden staging directory would be unreachable by all of them.
   Marker *files* may keep dot-names, because they are machine state and not prose.

## Filename grammar — per quadrant

Always **kebab-case**, always `.md`, never spaces, never capitals except `README.md` and
`CHANGELOG.md`.

| Directory | Grammar | Good | Bad |
|---|---|---|---|
| `tutorials/`, `how-to/` | imperative verb first | `seed-a-partition.md` | `partition-seeding.md` |
| `reference/` | bare noun | `signal-fields.md` | `understanding-signals.md` |
| `explanation/` | noun phrase, or a `why-` | `why-there-is-one-writer.md` | `read-this-about-writes.md` |
| `work/` | `YYYY-MM-DD-slug` | `2026-01-15-classifier-feasibility.md` | `classifier-research.md` |
| `adr/` | `NNNN-slug` (frozen) | `0069-history-is-user-data.md` | — |

A date in a quadrant filename, or a missing date in `work/`, is a placement error — go back to
`placement.md` gate 1.

## Depth, grouping, size

- **Depth cap: three path segments below the docs root** (`<docs>/how-to/storage/seed-a-partition.md`).
  Four under `work/`, which needs a subject level and a date level. Deeper than that and the path
  stops being navigable and starts being a filesystem.
- **Flat until 12 files in a directory.** At 12, group **by subject** — `how-to/storage/`,
  `reference/channels/`. **Never by document kind.** Kind is already the directory you are in.
- **Size:** under ~60 lines, it is a section of another document, not a document — merge it. At
  **400 lines, split** on the natural subject boundary. At **600 lines it is a defect**: nobody
  reads it linearly and nobody can tell what is stale.

## Every directory README is a complete map

A directory's `README.md` lists **every file** in it, one line of purpose each. (Subdirectories
answer for themselves and are not listed file-by-file — each has its own README.) Not the
interesting ones. Every one. An entry that is missing is a document nobody will find, and one that
a later agent will helpfully recreate somewhere else.

The failure mode, observed live: a docs root README tabling ten entries while the directory held
seventeen — seven directories of real content, invisible to anyone reading the map. **A partial map
is worse than none, because it is read as complete.**

One purpose statement per directory, and it is the README's first line. A second one — a `.purpose`
file beside it — was tried and removed: it had no reader, and dot-files are invisible to ripgrep.

## The freeze list — derived, never judged

A path is **frozen** — never moved, renamed, reformatted, or given frontmatter — if **any** holds:

1. the build references it as an embedded resource, content item, or packaged data file;
2. its literal path appears in project configuration or agent-instruction files;
3. it is named as a resume anchor or authoritative specification by any agent's memory or
   instruction set;
4. it appears in an open PR or issue body, or an open PR's changed-file list;
5. it is under `adr/`;
6. it is not `.md`.

**Derive this list from the build and from live work — never by reading a file and forming an
opinion.** Where the project has no tool for it, derive it by hand: grep the build files for the
docs root, grep the project's configuration and instruction files for docs paths, and list open PRs
and issues. Write the result down before you move anything.

**Why rule 1 is not negotiable.** A markdown file the build embeds and the application parses at
runtime is production data. Moving it breaks the build — loudly, which is survivable. Reformatting
one, or adding frontmatter to it, corrupts the parsed data *silently* and fails from a static
initializer at runtime. That is the worst failure shape available.

**Why rule 2 is its own PR.** Renaming a file pinned by project configuration or by agent
instruction files means editing those sources and re-running the scaffolder, which rewrites the
agent discovery file and every scoped instruction file. That change is never part of a migration PR.

## Anti-patterns

Recognise the shape, not the wording. Every row below was observed in a real corpus.

| Anti-pattern | Why it hurts |
|---|---|
| Undated work record at the docs root | Reads as current documentation forever; nobody dares delete it |
| Two homes for one subject | Neither is authoritative; both drift; readers pick the wrong one |
| Grouped by document *kind* (`plans/`, `research/`, `designs/`, `incidents/`) | Kind is not a subject. All four are `work/` |
| README that is not a complete map | Unlisted means unfindable means duplicated |
| N review documents describing one subject | At least one of them describes it falsely |
| Frontmatter or reflow on a frozen file | Silent production-data corruption, discovered at runtime |
| Prose in a hidden directory | ripgrep skips it; the default search of every agent misses it |
| A second hand-maintained purpose statement beside a README | Diverges within a month; then which one is true? |
| Deep nesting to express taxonomy | Five levels is a filesystem, not a map. Cap is three |
