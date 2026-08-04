# Brand — the AiRaccoon mark

<p align="center">
  <img src="previews/a-circuit-mask-420.png" width="180" alt="AiRaccoon: a circuit-masked raccoon keeping a memory chip">
</p>

Hand-authored SVG for the AiRaccoon mark. Nothing is traced from a photograph, so no
third-party licence attaches to it. It is a sibling of the
[ai-badger mark](https://github.com/Arasz/ai-badger/tree/main/docs/brand) — same canvas,
same composition grammar — so the two projects read as one family while staying distinct.

Full-size render: [`previews/a-circuit-mask-420.png`](previews/a-circuit-mask-420.png).

## The mark — Memory Keeper

The raccoon's bandit mask *is* the circuit trace. The mask runs across the face as it
naturally does — dark band, white spectacles around the eyes — and the band carries thin
orange circuit traces with nodes, the eyes being the trace's two lit endpoints. The
raccoon peeks over a **memory chip**: the die carries a "MEM" wordmark traced from
Helvetica Bold (converted to outlines, never `<text>`), and the pin pads form a uniform
grid. Paws hook over the chip's edge, the same gesture the badger uses — here the raccoon
reaches into the chip to keep hold of what it stores.

**What it is saying.** The raccoon is the animal the project is named for, drawn as a
character that looks back at you. The bandit mask — the one feature everyone recognises —
is the technology, so the AI idea survives scaling down. The memory chip is where the
server actually lives: the store itself, labelled MEM. Orange is the accent (the trace
colour), deliberately distinct from ai-badger's teal while sharing the same dark field.

### Design decisions

- **Orange lines, not teal** — the accent was moved from teal to `#FF8C42` so the mark
  stands apart from ai-badger while staying in the same family.
- **Chip instead of console** — the terminal in the badger mark is replaced by the memory
  chip: the product *is* the store, not a shell running it.
- **MEM as traced outlines** — written as normal text first, then traced to paths
  (`fontTools`, Helvetica Bold) and used as the SVG base; the letterforms are converted
  to outlines so the mark renders identically everywhere. Sized small and centred on the
  die so the chip reads as hardware, not a label.
- **Face iterated against a reference photo** — three improvement rounds with a vision
  model (rising temperature) against a raccoon photo: continuous mask band, sharp upright
  ears, bigger close-set eyes, white muzzle bridge between the eyes, thin spaced whiskers.

| | |
|---|---|
| **Canvas** | 256×256, rounded-square field, `rx="56"` |
| **Accent** | `#FF8C42` orange — traces, eyes, chip, pads, hairlines |
| **Field** | `#16202C` → `#0A1017` vertical gradient |
| **Fur** | `#F2F5F8` → `#C0CCD9` vertical gradient (raccoon grey) |
| **Mask** | `#161C24` ink, white spectacles `#F2F5F8` |
| **Chip** | `#1C2634` → `#111823` body, `#0D141D` die, `#FF8C42` MEM + pads |
| **Best at** | 96 px and up |

## Alternative propositions

Two earlier propositions remain in this directory if a variant is ever wanted:

- **`proposition-b-memory-hoarder.svg`** — a softer, story-forward raccoon cradling a
  glowing memory crystal (teal family). Warm companion mark.
- **`proposition-c-mask-mark.svg`** — the mask as a flat minimal glyph, built for small
  sizes (teal family). Favicon candidate.

Both keep the dark field and rounded-square canvas; neither is the primary mark.

## Using the mark

**Clear space** — leave at least 1/8 of the mark's width empty on every side. The
rounded-square field is part of the mark; do not crop to the head.

**Do not** recolour the accent per context, stretch the square, add a drop shadow, or set
the raccoon over a photograph. If a variant is needed that this file does not cover, add
it to this directory rather than editing a copy at the call site.

## Not yet drawn

None of these blocks using the mark; each is worth doing when something actually needs it.

- **A wordmark lockup** — mark plus "AiRaccoon" set horizontally, for a social card. The
  letterforms must be converted to outlines rather than left as SVG `<text>`.
- **A monochrome variant** — one flat colour, for places that will not take a full-colour mark.
- **A small-size variant** — the full mark carries detail below ~96 px. A favicon and a
  plugin icon need the head alone, simplified, without the chip.
- **Raster exports** — PNG at 16/32/180/512 px, since GitHub social previews and OS app
  icons do not accept SVG.

## Editing

To preview a change on macOS without opening a design tool:

```bash
qlmanage -t -s 420 -o /tmp docs/brand/proposition-a-circuit-mask.svg
```

Check any edit at 16 px too — that is where geometry mistakes become visible.
