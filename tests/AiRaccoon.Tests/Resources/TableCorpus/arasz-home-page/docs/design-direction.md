# arasz.me — UI Design-Direction Document

Research + direction only. No repository code was modified to produce this document — see
the read-only clone at `arasz-home-page/` and the reference frontend at
`job-search-ai-assistant/src/frontend/` for the source material this direction is built on.
This is the Phase‑1 deliverable; a Phase‑2 engineering pass builds it in Angular.

## 0. Brief, subject, and the one sentence this page has to prove

**Subject**: Rafał Araszkiewicz, senior .NET/AWS/AI engineer, currently building his own
production-grade job-search assistant while contracting. **Audience**: a recruiter or
hiring manager scanning in under 10 seconds, plus a technical reviewer (peer engineer,
tech lead) who will scroll further and actually read the stack. **Job of the page**:
prove *senior, hands-on, currently-shipping engineer* fast, then hand the visitor a low
friction way to reach him.

**Current state** (`arasz-home-page/`): a VS Code-blue (`#007acc`) dark template — generic
"Welcome" hero, a header with GitHub/LinkedIn links, one hero paragraph, and a contact
form behind a pulsing button. JetBrains Mono is already loaded. There is no experience
proof, no projects section, and the blue accent reads as "default dark-mode starter,"
not as a considered identity.

**Governing idea for the refactor**: stop treating the terminal font as a color choice
and start treating it as the *interface's native language*. The whole page reads as one
continuous local shell session — the same session, top to bottom — anchored by a
persistent prompt string that reappears as the structural heading for every section. Each
heading is a real, section-describing command (`whoami`, `cat experience.log`,
`ls projects/`, `./contact --request`), not a decorative label. That's the signature
element (§1.4) and it is the one place this design spends its boldness. Everything else —
palette, motion, card chrome — stays quiet and disciplined around it.

### Self-critique against the generic-AI defaults

Before locking this in, I checked it against the three clusters AI-generated design
gravitates to by default:

- **Cream + serif + terracotta** — ruled out by the brief's own hard constraint
  (monochrome dark). Not a live risk.
- **Near-black + one neon accent (acid green / vermilion)** — the brief explicitly allows
  "at most one restrained accent treatment, default to pure B&W." The generic move here
  is to reach for a hue anyway and call it "restrained." I didn't: the one accent this
  design uses is **tonal inversion** (swap `--text-primary` and `--surface-0`), applied
  only to the primary CTA and the active prompt cursor. No hex hue is introduced anywhere.
  That's a genuine constraint, not a hedge.
- **Broadsheet: hairline rules, zero border-radius, dense newspaper columns** — this is
  the trap closest to a terminal aesthetic, and my first pass leaned into it (square
  corners everywhere). I revised it: real terminal emulators (iTerm, Alacritty, Warp) have
  *soft*-rounded window chrome, not razor corners, and this page borrows that specific
  detail — a small consistent radius (4–6px, §1.1) on panels, cards, inputs, and the CTA —
  instead of the broadsheet's zero-radius default. Layout stays single/double-column with
  generous whitespace, not dense multi-column text, because "clean, minimal" is a hard
  constraint and newspaper density fights it.
- **Numbered sections (01/02/03)** — considered for the projects grid and rejected: the
  three projects have no real sequence, so numbering them would be decoration wearing the
  costume of information. I used a **status pill** (PUBLIC / PRIVATE / SELF) instead — a
  label that's actually true of each project. The one place a numbered/sequential device
  *is* honest is the experience timeline (§3.2), because career history is genuinely
  ordered — so it keeps a date axis, not decorative numerals.

---

## 1. Design language

### 1.0 Type system — single family, multi-weight, not a display/body pair

The brief asks to lean into JetBrains Mono rather than treat it as a body-copy fallback.
The deliberate choice here is **one typeface for the entire page**, carrying the full
hierarchy through weight, size, and tracking instead of a display/body pairing. This is
the opposite of the safe move (pair mono with a humanist sans for readability) and it's
justified specifically for this subject: an all-monospace marketing page reads as
*authored by someone who lives in a terminal*, which is the actual claim being made.
JetBrains Mono ships weights 100–800 plus italics, which is enough range to carry a full
scale without a second family.

Loaded weights: 400 (body), 500 (labels, tags, nav), 600 (headings, prompt eyebrows), 700
(hero headline and the neofetch stat numbers only — reserved, high-impact use).

| Token         | Size               | Weight  | Line-height | Use                                                                       |
|---------------|--------------------|---------|-------------|---------------------------------------------------------------------------|
| `--text-3xl`  | 64px / 40px mobile | 700     | 1.05        | Hero stat numbers (desktop only)                                          |
| `--text-2xl`  | 48px / 32px mobile | 700     | 1.1         | Hero headline                                                             |
| `--text-xl`   | 32px / 24px mobile | 600     | 1.15        | (reserved, not used at launch)                                            |
| `--text-lg`   | 24px / 20px mobile | 600     | 1.25        | Section headings (h2)                                                     |
| `--text-md`   | 18px               | 400     | 1.5         | Lead paragraph, hero one-liner                                            |
| `--text-base` | 16px               | 400     | 1.6         | Body copy                                                                 |
| `--text-sm`   | 14px               | 400/500 | 1.5         | Captions, form labels, card meta                                          |
| `--text-xs`   | 12px               | 500     | 1.4         | Prompt eyebrows, tags, status pills — `letter-spacing: 0.04em`, uppercase |

Tracking: `0` on body and headings; `0.02em–0.04em` on every all-caps label (eyebrows,
tags, status pills, nav) — the one typographic tic that reads as "man page," used
consistently rather than everywhere.

### 1.1 Palette — grayscale ramp, one inversion accent, no hue

All values verified against WCAG 2.1 AA with actual relative-luminance contrast math (not
eyeballed — see the computed ratios below). `text-disabled` is intentionally sub-AA: it's
only ever used on `disabled`/`aria-disabled` controls, which WCAG exempts from the
contrast requirement.

| Token                | Hex                          | Role                                         |
|----------------------|------------------------------|----------------------------------------------|
| `--surface-0`        | `#0b0b0c`                    | Page background                              |
| `--surface-1`        | `#151517`                    | Card / panel background                      |
| `--surface-2`        | `#1e1e21`                    | Hover surface, popover, modal body           |
| `--border-subtle`    | `#26262a`                    | Hairline dividers, meter track outline       |
| `--border-default`   | `#3a3a3f`                    | Card borders, input borders                  |
| `--border-strong`    | `#6b6b70`                    | Focus rings, active dividers, emphasis rules |
| `--text-primary`     | `#f4f4f5`                    | Primary text, headings                       |
| `--text-secondary`   | `#b4b4b8`                    | Body copy, secondary content                 |
| `--text-muted`       | `#838388`                    | Captions, meta, placeholder text             |
| `--text-disabled`    | `#4b4b4f`                    | Disabled control text (exempt from AA)       |
| `--accent-invert-bg` | `#f4f4f5` (= `text-primary`) | Fill for the one inversion accent            |
| `--accent-invert-fg` | `#0b0b0c` (= `surface-0`)    | Text/icon on the inverted fill               |

Computed contrast (bg pair → ratio, AA body threshold is 4.5:1, AA large-text/UI is 3:1):

```
text-primary   on surface-0 → 17.90:1   (AAA)
text-secondary on surface-0 →  9.52:1   (AAA)
text-muted     on surface-0 →  5.22:1   (AA)
text-primary   on surface-1 → 16.59:1   (AAA)
text-secondary on surface-1 →  8.82:1   (AAA)
text-muted     on surface-1 →  4.83:1   (AA, held above the 4.5 floor on the darker
                                          surface deliberately — this is why text-muted
                                          is #838388 and not the more "expected" #6e6e6e)
border-strong  on surface-0 →  3.71:1   (AA — meets the UI-component 3:1 floor, used
                                          for focus rings / meter fills, never body text)
accent-invert-fg on accent-invert-bg → 17.90:1  (AAA, inversion CTA)
```

`border-default` and `border-subtle` are sub-3:1 by design — they're decorative dividers,
never load-bearing for reading text, so WCAG's non-text contrast exception applies.

### 1.2 Spacing scale

Harvested and extended from the reference frontend's `--spacing-*` aliases
(`job-search-ai-assistant/src/frontend/app/app.css`, which already runs 4/8/16/24/32/48):

```
--space-1   4px    inline icon gaps, tag padding
--space-2   8px    tight stacks (label → input)
--space-3  12px    meter row gaps, chip gaps
--space-4  16px    default component padding
--space-5  24px    card padding, form field stacks
--space-6  32px    inter-component gaps within a section
--space-7  48px    section internal top/bottom padding (mobile)
--space-8  64px    section internal top/bottom padding (desktop)
--space-9  96px    inter-section rhythm (desktop) — the vertical "scroll distance"
                    between hero/experience/projects/contact
```

### 1.3 Borders, radius, elevation-via-contrast

- **Radius**: `--radius-sm: 4px` (tags, pills, meter segments), `--radius-md: 6px` (inputs,
  buttons, cards), `--radius-lg: 10px` (modal, the neofetch hero panel) — soft terminal-
  window chrome, not zero-radius broadsheet, not the token-value harvested from the React
  app's `--radius-sm:6px/--radius-md:8px` scale, scaled down slightly for a denser,
  more "CLI" feel.
- **Dividers**: 1px solid, `border-subtle` for internal rules (inside a card), `border-
  default` for card/section boundaries, `border-strong` reserved for focus rings and the
  active meter fill.
- **Elevation** is expressed as a 3-step surface ramp, not shadow: resting content sits on
  `surface-0`; a card is `surface-1` with a `border-default` edge; a genuinely "raised"
  element (hover state, popover, the neofetch panel) steps to `surface-2`. Box-shadow is
  reserved for true overlays only — the contact modal and the tooltip — at
  `0 12px 32px rgba(0,0,0,0.45)`, because shadows barely register against a near-black
  page and shouldn't be used to fake elevation that the surface ramp already communicates.

### 1.4 The signature element — the prompt eyebrow

Every section is introduced by a small component, `PromptEyebrow`, styled as a literal
shell prompt: `rafal@arasz:~$ <command>`, `text-xs`, `text-muted` for the
`user@host:~$` part, `text-primary` for the command, with a fixed-width caret block after
it (`▍`, `--accent-invert-bg` fill) that blinks 3 times on first scroll-into-view and then
holds steady solid — indefinite blinking is an accessibility anti-pattern, so it's capped.
The commands are real: `whoami` (hero), `cat experience.log` (experience), `ls projects/`
(projects), `./contact --request` (contact). This is the one recurring device the whole
page is built around, and it's also what makes each section heading load-bearing content,
not decoration — this repo's own design instinct ("structure is information, not
decoration").

### 1.5 Motion principles

- `prefers-reduced-motion: reduce` disables everything below except instant opacity swaps
  on hover/focus — no exceptions.
- **One orchestrated load sequence**: the hero's neofetch panel (§2, §3.1) types its
  field values in top to bottom, ~40ms stagger per line, ≤600ms total, ending with the
  3-blink caret described above. This is the only "wow" moment on the page.
- **Hover/focus micro-interactions**: 120–160ms ease-out; transforms limited to 1–2px
  translate or a background/foreground swap (the inversion accent). Monospace text is
  never scaled on hover — subpixel scaling blurs fixed-width glyphs.
- **Scroll reveal**: each section fades + translateY(8px) once, on first intersection,
  200ms, children staggered 60ms apart, capped at 4 staggered children (a wall of
  sequential fades reads as templated).
- **Explicitly cut**: scanline overlays, CRT flicker, matrix-rain, glow/neon box-shadow
  pulses, cursor-follow effects. These are the terminal-aesthetic clichés that read as
  "AI-generated" rather than "engineer's site" — the self-critique in §0 already flagged
  the risk of overdoing the metaphor; this is where that discipline is enforced.

---

## 2. Page structure — recruiter-scan order

```
┌─────────────────────────────────────────────────────────────┐
│ TOP BAR   rafal@arasz:~$ _   About  How I work  Experience  Projects  Contact   [GH] [in] │
├─────────────────────────────────────────────────────────────┤
│ HERO      ~$ whoami                                          │
│           neofetch-style identity panel + 1-line value prop  │
│           [ Contact me ]  [ View GitHub ↗ ]  [ Request CV ]  │
├─────────────────────────────────────────────────────────────┤
│ HOW I WORK   ~$ cat principles.md                             │
│   4 principles, each linked to a public MIT artifact          │
├─────────────────────────────────────────────────────────────┤
│ EXPERIENCE   ~$ cat experience.log                            │
│   Skill matrix  |  Career timeline  |  Cert badge  |  GitHub  │
├─────────────────────────────────────────────────────────────┤
│ PROJECTS   ~$ ls projects/                                    │
│   [ ai-badger ]  [ ai-raccoon ]  [ job-search-ai-assistant ]  [ home-page ]  │
├─────────────────────────────────────────────────────────────┤
│ CONTACT    ~$ ./contact --request                             │
│   Contact form (modal-triggered) + Request CV                 │
├─────────────────────────────────────────────────────────────┤
│ FOOTER     social links · © · "built with Angular, on Vercel" │
└─────────────────────────────────────────────────────────────┘
```

Rationale, most-important-first: **(1) Hero** answers "is this person senior and in my
stack" in one glance — no "Welcome" throat-clearing. **(2) Experience** is scannable proof
(skills, tenure, certification) before any prose — a recruiter can leave satisfied at this
point. **(3) Projects** is evidence of shipped work for anyone who scrolls further.
**(4) Contact** is the conversion point, placed last so it's reached only after the case
has been made, but it's never more than one section-scroll away from the always-visible
top-bar CTA affordance (nav item + persistent header, no floating action button needed —
a floating CTA would fight the "clean, minimal" constraint).

---

## 3. Component & widget specs

### 3.1 Hero — `HeroPanel` (replaces current `Hero`)

**Purpose**: prove seniority + stack fit in the first viewport, in the vernacular of the
signature element (a `neofetch`/`fastfetch`-style system-info panel instead of the generic
"big number + label + gradient" default the brief itself calls out as the template
answer).

```
  rafal@arasz:~$ whoami                                    [cursor: ▍ x3 blink → hold]

  ┌──────────────────────────────────────────────────────────┐
  │  role        Senior Software Engineer — .NET / AWS / AI   │
  │  focus       Fullstack .NET · Angular · distributed        │
  │              systems · AI/agent engineering                │
  │  cloud       AWS (Certified Solutions Architect) · Azure    │
  │  toolchain   Terraform · CI/CD · Cosmos DB · GitHub Actions │
  │  status      Open to senior/staff contract & FTE roles      │
  └──────────────────────────────────────────────────────────┘

  Accelerate delivery with a contractor who ships the whole stack —
  backend, frontend, cloud infra, and the CI/CD that gets it to prod.

  [ Contact me ]   [ View GitHub ↗ ]   [ Request CV ]
```

- **Layout**: panel is `surface-2`, `radius-lg`, `border-default`, max-width ~720px,
  centered; fields are a label/value grid (`text-muted` label column, fixed width ~90px;
  `text-primary` value column). One-line value prop sits below the panel in `text-md`,
  `text-secondary`. CTA row below that.
- **States**: `loading` (load sequence typing, §1.5) → `settled` (static, caret holds
  solid). No data dependency — content is static/authored, so there's no error state.
- **Data source**: static, hand-authored (`src/app/data/hero.ts`). The `status` line is
  the one field expected to change over time (open/closed to work) — call it out to
  content owner as the one hero field that needs periodic upkeep.

### 3.2 Experience section — four widgets under one `PromptEyebrow`

All four widgets share one monochrome encoding vocabulary, per the brief's "no color, so
value/texture/size/position" instruction:

- **Value** (grayscale lightness) = intensity/emphasis (e.g., a filled meter segment is
  `text-primary`, an empty one is `border-subtle`).
- **Texture** (a 45° hatch, `repeating-linear-gradient`) = a secondary/derived quantity
  layered on top of value, never standing alone — used on the certification badge and, if
  a chart is direct-labeled anyway (≤4 segments), texture is optional; used only where it
  adds a real print/forced-colors fallback, per the dataviz non-negotiables.
- **Size** = magnitude (meter fill width, timeline segment length).
- **Position** = sequence/category (timeline x-axis, matrix row grouping).

No categorical-hue palette is used anywhere in these widgets — there is nothing to run
`validate_palette.js` against because no hue exists; the equivalent check applied instead
was the same six-check rigor translated to value: every "series" (a skill row, a language
segment, a career era) must still be distinguishable without relying on adjacent value
steps alone, which is why every mark is also **direct-labeled** (a numeric fraction, a
year range, a percentage) rather than relying on bar length alone.

#### 3.2.1 `SkillMatrix`

**Purpose**: fastest possible "does he know X" scan. **Layout**:

```
  Languages & Frameworks
  .NET / C#          [■■■■■■■■■■] 10/10
  Angular            [■■■■■■■■□□]  8/10
  TypeScript         [■■■■■■■■□□]  8/10

  Cloud & Infrastructure
  AWS (Solutions Architect)  [■■■■■■■■■□]  9/10
  Azure                      [■■■■■■□□□□]  6/10
  Terraform                  [■■■■■■■□□□]  7/10

  AI / Agent Engineering
  LLM tool orchestration     [■■■■■■■□□□]  7/10
  Agentic workflows          [■■■■■■■□□□]  7/10
```

- **Visual encoding**: each meter is 10 fixed-width segments, `2px` gap between segments
  (dataviz mark spec — a surface-color gap between adjacent fills, never abutting fills),
  filled = `text-primary`, empty = `border-subtle` outline only (not a second gray fill,
  to keep the value contrast crisp between filled/empty). Numeric fraction is *always*
  shown next to the meter — never color/length-only — both because two adjacent
  proficiency levels can be visually hard to tell apart at a glance and because it's the
  direct-label rule from the dataviz procedure.
- **Markup, not glyphs**: rendered as 10 `<span>` segments via CSS, *not* literal Unicode
  block characters in text content — screen readers should get one accessible label
  ("Angular, proficiency 8 of 10") via `aria-label` on the row, with the segments marked
  `aria-hidden="true"`. The ASCII-bracket look above is the *visual* target, not the DOM.
- **States**: static content, no loading/error state.
- **Data source**: static, hand-authored (`src/app/data/skills.ts`), grouped by category.

#### 3.2.2 `ExperienceTimeline`

**Purpose**: seniority/tenure proof. This is the one widget where a sequential/numbered
device is *honest* (career history is genuinely ordered by date), so it keeps a real date
axis rather than a decorative index.

```
  ~$ cat experience.log

  2013 ───────────────────────────────────────────────────────── 2026
       [ Company A ]──[ Company B ]────[ WithSecure ]──[ Independent/contract ]
        Backend eng    Senior .NET     Senior Engineer   .NET·AWS·AI contractor
        2013–2016      2016–2020       2020–2024          2024–present
```

- **Visual encoding**: a single horizontal `border-default` rule is the "main branch";
  each role is a bracketed segment (`border-default` box) whose **length = size encodes
  duration**, **position on the rule = the date range**, and **position above/below the
  line alternates** to prevent label collision on adjacent short roles. The current role
  is the only segment filled `surface-2` instead of outline-only — value distinguishes
  "now" without needing a color. Every segment is direct-labeled (company/role + year
  range); no legend is needed because there are only 3–5 segments and each is unique.
  Below ~640px viewport width the timeline rotates 90° into a vertical stack (still a
  single ordered rule, now top-to-bottom) rather than compressing horizontally into
  illegible segments.
- **Content assumption flagged for the copy pass**: the exact start year, employer names,
  and role titles above are **placeholders** — this document does not invent Rafał's
  employment history. The real values should come from the same canonical work-history
  data he already maintains as `CvTemplate` work-experience entries in
  `job-search-ai-assistant` (see §6) rather than being re-typed by hand for the site.
- **States**: static, no loading/error.
- **Data source**: static (`src/app/data/career-timeline.ts`) at launch; see §6 for the
  longer-term option of sourcing this from the CV template data directly.

#### 3.2.3 `CertificationBadge`

**Purpose**: a single, concrete trust signal — AWS Certified Solutions Architect –
Associate.

```
  ┌╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱┐
  ╱  AWS CERTIFIED          ╱
  ╱  SOLUTIONS ARCHITECT    ╱
  ╱  — ASSOCIATE —          ╱
  ╱  Verify ↗               ╱
  └╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱╱┘
```

- **Visual encoding**: a bordered square/stamp, `border-strong`, with a subtle 45° hatch
  texture (`repeating-linear-gradient`, `border-subtle` on `surface-1`, low contrast —
  decorative only, so it's exempt from text-contrast rules) standing in for the "ink
  stamp" the badge metaphor implies, without introducing color. Text is `text-xs`,
  uppercase, tracked. If Rafał has a public Credly/AWS verification URL, "Verify ↗" links
  out; otherwise this line is omitted rather than linking to nothing.
- **States**: static. If a second certification is earned later, the widget should become
  a small horizontal row of badges rather than being redesigned — plan the layout for N=1
  today, N=2–3 later, not N=1 forever.
- **Data source**: static (`src/app/data/certifications.ts`) — name, issuer, date earned,
  optional verify URL.

#### 3.2.4 `GithubActivityWidget`

**Purpose**: a live, low-effort-to-maintain signal that the skill matrix isn't just
self-reported. **Data source**: GitHub's public REST API, fetched client-side (no OAuth —
LinkedIn's API needs OAuth and is explicitly deferred per the brief; GitHub's public read
endpoints don't):

```
GET https://api.github.com/users/{username}/repos?sort=updated&per_page=100
→ for each repo, GET repo.languages_url
→ sum bytes per language across all public, non-fork repos
→ top 4 languages by byte share, remainder folded into "Other"
   (dataviz non-negotiable: a 9th/5th series never becomes a new value step —
    it folds into Other)
```

```
  ~$ github --languages

  C#           [■■■■■■■■■■■■■■■■■■■■□░] 62%
  TypeScript   [■■■■■■□░░░░░░░░░░░░░░░] 19%
  HCL          [■■□░░░░░░░░░░░░░░░░░░░]  8%
  Other        [■□░░░░░░░░░░░░░░░░░░░░]  6%
                                     public repos: 14 · updated live
```

- **Visual encoding**: one horizontal stacked meter (not four separate bars) — segment
  **length = magnitude**, **position = fixed order** (largest first, never re-sorted on
  re-render so identity doesn't jump around between visits), every segment
  **direct-labeled** with name + percentage (≤4 segments, so no legend box is needed per
  the dataviz rule). Filled portion `text-primary`→`text-secondary` value step per segment
  rank (not a hue), gap between segments is the mandated 2px surface-color gap.
- **States** (this is the one widget on the page with real network risk, so it needs all
  four): `loading` — a skeleton meter with the eyebrow line reading
  `~$ fetching github stats...` (no spinner glyph — stay in the terminal vernacular);
  `loaded` — as above; `stale/rate-limited` — falls back to a small pinned JSON snapshot
  checked into the repo (`src/app/data/github-snapshot.ts`) with a visible
  `(cached — updated <date>)` note in `text-muted`, so the widget never shows a broken or
  empty state to a recruiter; `empty` (zero public repos) — not expected but handled by
  simply omitting the widget rather than rendering a hollow meter.
- **Implementation note**: GitHub's public REST endpoints send CORS headers permissive
  enough for a direct browser fetch (no proxy needed) — flagged here as an assumption the
  Phase-2 engineer should confirm against current GitHub API behavior before building,
  not treated as guaranteed. Unauthenticated rate limit is 60 req/hr **per visitor IP**,
  which is why the snapshot fallback exists rather than a server-side proxy — appropriate
  for a low-traffic personal site, not a general solution.

---

## 4. Projects section

**Purpose**: evidence of shipped work, three cards, one consistent card system, no
numbering (rejected in §0 as decoration masquerading as sequence). Status is communicated
by a **status pill**, which is real information the numbering wouldn't have been.

```
┌ PUBLIC ──────────────┐  ┌ PRIVATE ─────────────┐  ┌ SELF ────────────────┐
│ ai-badger             │  │ job-search-ai-        │  │ home-page             │
│                       │  │ assistant             │  │                       │
│ [one-line summary]    │  │ [one-line summary]    │  │ [one-line summary]    │
│                       │  │                       │  │                       │
│ .NET  Agents  AI      │  │ .NET  Angular  Azure   │  │ Angular  CSS  Vercel  │
│                       │  │ Cosmos DB              │  │                       │
│                       │  │                       │  │                       │
│ View repository ↗     │  │ Private repository —   │  │ (you're looking       │
│                       │  │ MVP in progress         │  │  at it)               │
└───────────────────────┘  └───────────────────────┘  └───────────────────────┘
```

- **Card shell**: `surface-1`, `border-default`, `radius-md`, `space-5` padding, equal
  height in a 3-column grid (desktop) collapsing to 1 column (mobile), consistent
  internal stack: status pill → title → summary → tag chips → footer line.
- **Status pill** (`text-xs`, uppercase, tracked, `border-default` outline, no fill):
  `PUBLIC`, `PRIVATE`, `SELF`. This is the only differentiator needed between cards — no
  icon-only color coding, so it stays legible in monochrome and to screen readers (it's
  real text, not a colored dot).
- **Tag chips** (`TagChip`, shared with the skill matrix's category labels for visual
  consistency): `text-xs`, `border-subtle` outline pill, `radius-sm`, no fill — reads as a
  man-page flag list, not a colorful marketing badge row.
- **Footer line — this is where the three cards deliberately diverge, per the brief's
  hard rule**:
  - `ai-badger` (public): a real link, `text-primary`, `↗` suffix glyph, underline on
    hover/focus only (not resting) → `View repository ↗`.
  - `job-search-ai-assistant` (private, MVP soon): **plain, non-interactive text**,
    `text-muted`, *not* a link, *not* a button, *not even a disabled-styled button* — the
    brief is explicit that no access-request affordance should exist. Render it as a
    `<span>`, not an `<a>` or `<button>`, so no interactive semantics leak in by accident.
  - `home-page` (this site): a small self-aware line acknowledging the visitor is on it
    right now — no link needed since there's nowhere else to go. Keep this to one short
    line; it's a wink, not a bit.
- **Content note**: the one-line summaries above are placeholders (`[one-line summary]`)
  — this design document does not invent product descriptions or claim capabilities for
  `ai-badger` that weren't verified. See §5 for the copywriting brief that should fill
  these in from source of truth (the repos' own READMEs).

---

## 5. Contact — form, buttons, and their states

Three components, one shared visual language (inversion accent = the primary action in
each):

### 5.1 `ContactMeButton` (redesign of the existing pulsing VS-Code-blue button)

| State          | Treatment                                                                      |
|----------------|--------------------------------------------------------------------------------|
| Default        | Outline button, `border-strong`, `text-primary`, `surface-1` fill, `radius-md` |
| Hover          | Inverts: `accent-invert-bg` fill, `accent-invert-fg` text (the one accent)     |
| Focus-visible  | 2px `border-strong` ring, offset 2px — never color-only                        |
| Active/pressed | Inversion holds, 1px translateY                                                |
| Ambient (idle) | A slow (4s), subtle border-opacity breathe (0.6→1.0), **not** the              

current box-shadow ring pulse — same intent (draw the eye) at a fraction of the visual
noise, and fully disabled under `prefers-reduced-motion` |

### 5.2 `ContactForm` (redesign of the existing modal)

Reuses the existing `Tooltip` component for field hints (already in the codebase — no new
primitive needed there).

| State  | Treatment                                                                       |
|--------|---------------------------------------------------------------------------------|
| Closed | Not rendered (current behavior — modal mounts on request)                       |
| Open   | `surface-2` panel, `radius-lg`, `border-default`, backdrop `surface-0` at 70% + 

blur (keep the existing blur — it already reads as intentional depth, not template
default); focus-trapped, `Esc` closes |
| Field invalid | `border-strong` outline on the field (not red) + inline `text-muted`
helper line below, prefixed `!` glyph — value/weight carries the warning, not hue |
| Submitting | Submit button label → `Sending…`, disabled, no spinner (a slow caret-blink
on the button label keeps it in the terminal vocabulary) |
| Success | Form content is replaced in place by a terminal-style stdout line:
`✓ sent — I'll reply within 2 business days`, `text-primary`, modal auto-dismisses
after a few seconds or on click |
| Error (submit failed) | Form stays populated (never lose input), inline line above the
submit button styled as stderr: `error: message failed to send — try again or email
  directly`, `text-primary` on a `border-strong`-left-accented block (a left rule, not a
red fill, carries "this is an error") |

### 5.3 `RequestCvButton` (wired to CV-request backend)

A modal-triggered form that collects the requester's email and a reason for the CV request.

| State                                | Treatment                                                                |
|--------------------------------------|--------------------------------------------------------------------------|
| Enabled (current, shipped in PR #79) | Outline button, same visual family as `ContactMeButton` but outline-only 

on hover too (no inversion) — it's the secondary action, so it should never visually
outrank `ContactMeButton` |
| Data collection | `requesterEmail` and `requestReason` (free-text, validated for non-empty,
max-length enforced on backend) are collected, escaped, and stored as an issue on GitHub
with close reminder; `honeypot` field prevents automated form submission |

---

## 6. Content & LinkedIn-optimization notes (for the copy pass)

- **Headline keywords** (surface these verbatim somewhere in the first screen — hero
  panel `role`/`focus` fields, LinkedIn headline, and page `<title>`/meta description
  should all converge on the same phrase set so search and recruiter keyword-scanning
  agree): *Senior Software Engineer*, *.NET*, *C#*, *AWS Certified Solutions Architect*,
  *Angular*, *Azure*, *Terraform*, *distributed systems*, *CI/CD*, *AI/agent engineering*.
- **Summary framing**: lead with the contractor value proposition (ships the *whole*
  stack — backend, frontend, cloud infra, CI/CD), not a chronological "I have worked at."
  Recruiters scanning for a specific gap (e.g., "need Angular + AWS") should be able to
  confirm fit from the hero panel alone, without reading the timeline.
  Structure sentences here around **impact/evidence, not job titles**: what shipped, what
  scale, what outcome — a certified AWS Solutions Architect claim is already strong
  evidence and doesn't need adjectives stacked on top of it.
- **Impact/evidence phrasing pattern** (apply to every project summary and every
  timeline segment caption): *[what was built] using [stack] to [measurable/verifiable
  outcome]* — not *"responsible for"* or *"worked on."* Example shape: "Built a
  multi-agent job-search pipeline on .NET + Azure Functions, replacing a fully manual
  application workflow with a state-machine-tracked one." Avoid claims this document
  hasn't verified (exact metrics, employer names, dates) — flag them for Rafał to fill in
  rather than inventing plausible-sounding numbers.
- **Active voice, plain verbs, no filler** — matches this repo's own writing style
  (CLAUDE.md's "minimal comments" instinct generalizes to marketing copy too: say the
  thing once, precisely, and stop).
- **Project card summaries**: one line each, written from the *visitor's* side — what the
  project does / why it exists — not from the implementation's side ("a monorepo with
  Domain/Api/Infrastructure layers" is not visitor-relevant; "an AI assistant that
  tailors CVs per job application and tracks the pipeline end-to-end" is).

---

## 7. Implementation notes for Angular (Phase-2 handoff)

### 7.1 Tokens → `src/styles.css`

Add every token from §1.1–§1.3 to `:root` in `src/styles.css`, replacing the current
`--bg-primary`/`--bg-secondary`/`--text-primary`/`--text-secondary`/`--accent-color`/
`--border-color` set (the `--accent-color: #007acc` line is the one being retired). Keep
the naming *semantic* (`--surface-0`, `--text-muted`, …) rather than palette-literal
(`--gray-800`), matching the pattern already established in the reference React app's
`app.css` (`--state-*-bg/fg` semantic pairs) even though this page has no state-machine
chips of its own. Add `--space-1`…`--space-9` and `--radius-sm/md/lg` alongside. Keep the
existing `@media (forced-colors: active)` blocks in every component — the current code
already does this well (contact-form, social-media-links, tooltip all have them) and
strict monochrome makes forced-colors mode nearly a non-issue, but the blocks cost nothing
to keep.

### 7.2 Components to refactor

- `app.html`/`app.ts` — extract the inline `<header>` into a new `SiteHeader` (top bar)
  component; it currently mixes routing shell and page chrome in one file.
- `Hero` → rebuild as `HeroPanel` per §3.1 (neofetch layout, load-sequence animation via
  a signal-driven step index, respecting `prefers-reduced-motion` through a
  `matchMedia` check in the component, not CSS alone, so the JS-driven stagger can be
  skipped entirely rather than just visually suppressed).
- `ContactForm`, `ContactMeButton` — restyle in place per §5.1–5.2; the existing
  `FormGroup`/`ReactiveFormsModule` structure and the `Tooltip` integration are sound and
  don't need architectural changes, only the CSS and the new success/error states (which
  need a small state signal added, e.g. `submitState = signal<'idle'|'submitting'|
  'success'|'error'>('idle')`).
- `SocialMediaLinks` — restyle monochrome, move into `SiteHeader` and `SiteFooter` (used
  in both places per §2).
- `Tooltip` — restyle only (monochrome background/border), reused as-is for both the
  contact form field hints (current use) and the new `RequestCvButton` disabled-state
  explanation (§5.3) — no new tooltip primitive needed.

### 7.3 New components/primitives to add

| Component                                                             | Notes                                                             |
|-----------------------------------------------------------------------|-------------------------------------------------------------------|
| `PromptEyebrow`                                                       | The signature heading (§1.4). Inputs: `command: string`. Owns the 
 3-blink-then-hold caret animation, gated on `prefers-reduced-motion`. |
| `SkillMatrix` + `MeterBar`                                            | `MeterBar` is the shared 10-segment primitive (§3.2.1),           

reused by `GithubActivityWidget`'s language meter (§3.2.4) with a variant prop for the
stacked/multi-series layout vs. the single-series skill row layout. |
| `ExperienceTimeline` | §3.2.2. Pure presentational component over `career-timeline.ts`
data; owns the horizontal↔vertical responsive layout switch. |
| `CertificationBadge` | §3.2.3. Presentational, `certifications.ts` data, designed for
N=1 today / N≥2 as a wrapping row later. |
| `GithubActivityWidget` | §3.2.4. Owns the fetch (Angular 22 `resource()`/`httpResource`
signal API against the `GithubStatsService`), the loading/loaded/stale states, and the
snapshot fallback import. |
| `GithubStatsService` | `src/app/services/github-stats.service.ts` — wraps the two-step
fetch (repos → per-repo languages) and the byte aggregation in §3.2.4; returns a typed
`LanguageShare[]`. |
| `TagChip` | Shared by `SkillMatrix` category labels and `ProjectCard` tech tags. |
| `StatusPill` | `PUBLIC`/`PRIVATE`/`SELF`, used only in `ProjectCard`. |
| `ProjectsSection` + `ProjectCard` | Data-driven from `projects.ts`; `ProjectCard` takes
a `footer` variant (`link` \| `plain` \| `self`) mapping to §4's three footer treatments
so the "no link, no button" rule for the private project is enforced by the component's
type signature, not by convention. |
| `RequestCvButton` | §5.3. Shipped in PR #79, wired to CV-request backend with `requestReason` field. |
| `SiteFooter` | New — currently the only footer content (social links) lives in the
header; §2 calls for a real footer with repeated social links + meta line. |

### 7.4 Data files (`src/app/data/`)

`hero.ts`, `skills.ts`, `career-timeline.ts`, `certifications.ts`, `projects.ts`,
`github-snapshot.ts` — all static, typed, hand-authored TypeScript modules (no CMS, no
backend — this is a static personal site). Content for these should come from the copy
pass in §6, and — for `career-timeline.ts` specifically — ideally from the same work-
history data Rafał already maintains as `CvTemplate` entries in
`job-search-ai-assistant`'s data model (`docs/data-model.md`), so career history has one
canonical source instead of being maintained twice. That's a nice-to-have alignment, not
a blocker: static data is sufficient for the initial build, and the job-search assistant's
API is private/single-user (`src/JobSearchAiAssistant.Api` — not designed for public,
unauthenticated cross-origin reads from a marketing site), so wiring the two together for
real would need either a small public read-only export or a build-time content sync, not
a live cross-app fetch. Flagged as a future idea, not part of this refactor's scope.

### 7.5 What stays out of scope for this document

No visual mockups/screenshots were generated (text + ASCII wireframes only, per the
deliverables list). No copy is final — every headline, summary, and career-timeline entry
above is a placeholder for the content pass in §6. No code in `arasz-home-page/` or
`job-search-ai-assistant/` was modified.
