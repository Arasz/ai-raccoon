---
name: ai-text-humanization
description: "Use when humanizing AI text or removing AI writing tells."
version: 1.0.0
metadata:
  hermes:
    category: creative
    tags: [writing, humanize, anti-ai-slop, stylometry, detection, prompt-engineering]
---

# AI Text Humanization & Detection Evasion

Research-grounded framework for transforming AI-generated text to sound naturally human, pass stylometric scrutiny, and eliminate instruction-tuning/RLHF artifacts. Grounded in 50+ peer-reviewed detection papers (2023–2026).

## Seven Quantitative Hard Rules

1. **Em Dash Cap**: Maximum **1 em dash per 300 words**. Zero em dashes for passages under 300 words.
2. **Semicolon Ban**: **0 semicolons** in non-academic/non-legal prose.
3. **Straight Quotes Only**: Enforce straight quotes (`"..."`) and apostrophes (`'`). Never curly quotes.
4. **Negation Framing Ban**: Purge all instances of *"Not just X, but Y"*, *"It's not about X, it's about Y"*, and false binary pivots.
5. **Sentence-Length Spread ($\Delta_{\text{len}}$)**: In any output $>80$ words:
   - $\text{Length}(\text{Longest Sentence}) - \text{Length}(\text{Shortest Sentence}) \ge 20 \text{ words}$.
   - Fewer than $50\%$ of sentences may sit in the $10\text{--}20$ word band.
6. **Transition Density Cap**: Maximum **1 formal transition word** per 250 words. Zero instances of *"Furthermore"*, *"Moreover"*, or *"Additionally"*.
7. **Clean Output**: Deliver only the humanized prose and audit metadata.

---

## The Nine Research-Grounded Levers

1. **Perplexity Injection**: Inject 1-2 contextually surprising, precise, or domain-specific words per paragraph.
2. **Burstiness Enforcement**: Force extreme sentence length dispersion (mix 3-word punchy lines with 35-word multi-clause sentences).
3. **Hedge Surgery**: Remove softeners (*"it is important to note"*, *"worth mentioning"*). Use direct assertion or genuine human uncertainty.
4. **Structural Flattening**: Convert bolded listicles and formulaic headers into organic narrative paragraphs.
5. **Specificity Insertion**: Anchor claims in exact numbers, named tools, proper nouns, and dates.
6. **Voice & Register Shift**: Add 1st person (*"I"*), self-corrections, parentheticals, and conversational tics.
7. **Discourse Coherence**: Purge formal logical connectors (*"Furthermore"*, *"Moreover"*); rely on natural semantic flow.
8. **Punctuation Normalization**: Cap em dashes, ban semicolons, and enforce straight quotes.
9. **RLHF Voice Stripping**: Strip polite assistant tone, unprompted pros/cons lists, and reassurance kickers (*"and that's okay"*).

---

## Two-Pass Rewrite & Self-Audit Protocol

1. **Scan Input**: Identify instances of the 12 Banned Vocabulary Clusters and structural/punctuation tells.
2. **Draft Rewrite**: Apply the 9 levers to transform prose, voice, and structure.
3. **Forensic Self-Audit**:
   - Write out sentence word counts: `[L1, L2, L3, ...]`.
   - Verify $\text{Max}(L) - \text{Min}(L) \ge 20$ words and $<50\%$ of sentences are 10–20 words long.
   - Count em dashes ($\le 1 / 300\text{w}$) and semicolons ($0$).
   - Verify zero banned vocabulary words remain.
4. **Deliver Output**: Present the finalized, humanized text.

---

## Reference Material

For complete empirical benchmarks, paper citations (2023–2026), detector mechanics (GPTZero, Binoculars, Pangram 3.0, PIFE), and dead-end analysis (homoglyphs, thesaurus slop), see:
- `references/llm-humanization-detection-2026.md`
