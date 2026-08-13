# LLM Humanization & Detection Research Summary (2026)

Grounded in 50+ peer-reviewed research papers (2023–2026) and detection technical reports.

## Key Statistical & Detection Metrics

1. **Perplexity ($PPL$) & Cross-Perplexity ($X-PPL$)**:
   - AI text: Low, uniform perplexity ($PPL \approx 20\text{--}47$).
   - Human text: High, variable perplexity ($PPL \approx 80\text{--}165$).
   - Detectors like **Binoculars** measure $X-PPL$ ratios between surrogate models.

2. **Burstiness ($\sigma_{\text{sentence\_len}}$)**:
   - AI text: Sentence lengths cluster narrowly around 15–20 words ($\text{Score} \approx 0.2\text{--}0.4$).
   - Human text: Alternates between punchy 3-word sentences and 35-word multi-clause sentences ($\text{Score} \approx 0.6\text{--}1.2$).

3. **RLHF / Instruction-Tuning Artifacts** (*arXiv:2605.19516, "Base Models Look Human"*):
   - Modern neural classifiers (GPTZero, Pangram 3.0, Ghostbuster) primarily detect RLHF artifacts (polite hedging, artificial balance, copula avoidance, inline bold lists) rather than statistical "AI-ness".

4. **Biases & False Positives**:
   - ESL / Non-native English writers are misclassified as AI over 61% of the time (*Liang et al., 2023*) due to lower vocabulary variance.

## The Nine Research-Grounded Levers

1. **Perplexity Injection**: Inject 1-2 unexpected, precise, contextually surprising words per paragraph.
2. **Burstiness Enforcement**: Enforce $\Delta_{\text{len}} \ge 20$ words between longest and shortest sentence; $<50\%$ in 10-20w band.
3. **Hedge Surgery**: Cut softeners ("important to note", "worth mentioning"); use direct assertion or authentic human uncertainty.
4. **Structural Flattening**: Convert bolded listicles and formulaic headers into narrative prose.
5. **Specificity Insertion**: Anchor abstract claims in exact numbers, named tools, proper nouns, and dates.
6. **Voice & Register Shift**: Add 1st person ("I"), self-corrections, parentheticals, and conversational tics.
7. **Discourse Coherence**: Purge "Furthermore/Moreover/Additionally"; rely on natural semantic flow.
8. **Punctuation Normalization**: Cap em dashes to max 1 per 300 words (0 under 300w); 0 semicolons; straight quotes only.
9. **RLHF Voice Stripping**: Strip polite assistant tone, unprompted pros/cons lists, and reassurance kickers ("and that's okay").

## Advanced Multi-Model Techniques

- **Base-Model Rewriting**: Passing AI text through non-instruction-tuned base models natively strips RLHF signatures.
- **Iterative Paraphrase Laundering**: Sequential cross-model passes exploit the laundering region (*PADBen arXiv:2511.00416*).
- **Best-of-N Selection**: Generating N candidate rewrites and selecting the one with the lowest surrogate detector probability (*arXiv:2506.07001*).

## Documented Dead Ends

- **Character Noise / Homoglyphs**: Normalized away by UTF-8 / NFKC pre-processors; destroys document readability.
- **Naive Thesaurus Swapping**: Creates "thesaurus slop" without changing syntactic dependency trees.
- **Simple Prompt Demands**: "Write naturally" fails because autoregressive decoding still samples from mode-collapsed distributions.
