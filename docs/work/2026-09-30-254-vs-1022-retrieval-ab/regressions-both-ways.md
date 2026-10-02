# Per-query regressions, both directions

Rule: per-query mean nDCG@5 over the 3 repeatedly identical runs; a regression is strictly below the other arm (the report.find_regressions convention). More than 5 regressions on either side flags owner review.

- chunk254 regressions vs chunk1022: **43**
- chunk1022 regressions vs chunk254: **14**
- owner-review flag: **FLAGGED**

## chunk254 regressions (chunk1022 higher mean nDCG@5)

| query | category | 254 nDCG@5 | 1022 nDCG@5 | delta | 254 hit@1 | 1022 hit@1 |
| --- | --- | --- | --- | --- | --- | --- |
| E002 | ADR (Decision) | 0.0000 | 1.0000 | -1.0000 | 0 | 1 |
| E007 | ADR (Context) | 0.0000 | 1.0000 | -1.0000 | 0 | 1 |
| E029 | ADR (Decision) | 0.0000 | 1.0000 | -1.0000 | 0 | 1 |
| E044 | ADR (Decision) | 0.0000 | 1.0000 | -1.0000 | 0 | 1 |
| E048 | ADR (Consequences) | 0.0000 | 1.0000 | -1.0000 | 0 | 1 |
| E050 | ADR (Decision) | 0.0000 | 1.0000 | -1.0000 | 0 | 1 |
| E073 | ADR (Context) | 0.0000 | 1.0000 | -1.0000 | 0 | 1 |
| E005 | ADR (Decision) | 0.0000 | 0.6309 | -0.6309 | 0 | 0 |
| E018 | ADR (Consequences) | 0.0000 | 0.6309 | -0.6309 | 0 | 0 |
| E021 | ADR (Consequences) | 0.0000 | 0.6309 | -0.6309 | 0 | 0 |
| E026 | ADR (Decision) | 0.0000 | 0.6309 | -0.6309 | 0 | 0 |
| E032 | ADR (Decision) | 0.0000 | 0.6309 | -0.6309 | 0 | 0 |
| E034 | ADR (Context) | 0.0000 | 0.6309 | -0.6309 | 0 | 0 |
| E039 | ADR (Consequences) | 0.0000 | 0.6309 | -0.6309 | 0 | 0 |
| E057 | ADR (Content) | 0.0000 | 0.6309 | -0.6309 | 0 | 0 |
| E063 | ADR (Consequences) | 0.0000 | 0.6309 | -0.6309 | 0 | 0 |
| E047 | ADR (Decision) | 0.3869 | 1.0000 | -0.6131 | 0 | 1 |
| E051 | ADR (Consequences) | 0.3869 | 1.0000 | -0.6131 | 0 | 1 |
| E041 | ADR (Decision) | 0.4307 | 1.0000 | -0.5693 | 0 | 1 |
| E001 | ADR (Context) | 0.5000 | 1.0000 | -0.5000 | 0 | 1 |
| E014 | ADR (Decision) | 0.5000 | 1.0000 | -0.5000 | 0 | 1 |
| E022 | ADR (Context) | 0.0000 | 0.5000 | -0.5000 | 0 | 0 |
| E038 | ADR (Decision) | 0.5000 | 1.0000 | -0.5000 | 0 | 1 |
| E043 | ADR (Context) | 0.5000 | 1.0000 | -0.5000 | 0 | 1 |
| E046 | ADR (Content) | 0.5000 | 1.0000 | -0.5000 | 0 | 1 |
| E054 | ADR (Consequences) | 0.5000 | 1.0000 | -0.5000 | 0 | 1 |
| E056 | ADR (Decision) | 0.5000 | 1.0000 | -0.5000 | 0 | 1 |
| E059 | ADR (Decision) | 0.5000 | 1.0000 | -0.5000 | 0 | 1 |
| E071 | ADR (Decision) | 0.0000 | 0.5000 | -0.5000 | 0 | 0 |
| E072 | ADR (Consequences) | 0.0000 | 0.5000 | -0.5000 | 0 | 0 |
| E027 | ADR (Consequences) | 0.0000 | 0.4307 | -0.4307 | 0 | 0 |
| E042 | ADR (Consequences) | 0.0000 | 0.4307 | -0.4307 | 0 | 0 |
| E055 | ADR (Context) | 0.0000 | 0.4307 | -0.4307 | 0 | 0 |
| E017 | ADR (Decision) | 0.0000 | 0.3869 | -0.3869 | 0 | 0 |
| E036 | ADR (Consequences) | 0.0000 | 0.3869 | -0.3869 | 0 | 0 |
| E075 | ADR (Consequences) | 0.0000 | 0.3869 | -0.3869 | 0 | 0 |
| E008 | ADR (Decision) | 0.6309 | 1.0000 | -0.3691 | 0 | 1 |
| E070 | ADR (Context) | 0.6309 | 1.0000 | -0.3691 | 0 | 1 |
| E089 | Non-file (hermes transcript) | 0.6309 | 1.0000 | -0.3691 | 0 | 1 |
| E009 | ADR (Consequences) | 0.5000 | 0.6309 | -0.1309 | 0 | 0 |
| E064 | ADR (Context) | 0.5000 | 0.6309 | -0.1309 | 0 | 0 |
| E068 | ADR (Decision) | 0.5000 | 0.6309 | -0.1309 | 0 | 0 |
| E011 | ADR (Decision) | 0.3869 | 0.5000 | -0.1131 | 0 | 0 |

## chunk1022 regressions (chunk254 higher mean nDCG@5)

| query | category | 254 nDCG@5 | 1022 nDCG@5 | delta | 254 hit@1 | 1022 hit@1 |
| --- | --- | --- | --- | --- | --- | --- |
| E004 | ADR (Context) | 1.0000 | 0.3869 | -0.6131 | 1 | 0 |
| E013 | ADR (Context) | 1.0000 | 0.4307 | -0.5693 | 1 | 0 |
| E060 | ADR (Consequences) | 1.0000 | 0.4307 | -0.5693 | 1 | 0 |
| E078 | Non-file (hermes transcript) | 1.0000 | 0.4307 | -0.5693 | 1 | 0 |
| E058 | ADR (Context) | 1.0000 | 0.5000 | -0.5000 | 1 | 0 |
| E067 | ADR (Context) | 1.0000 | 0.5000 | -0.5000 | 1 | 0 |
| E082 | Non-file (hermes transcript) | 0.5000 | 0.0000 | -0.5000 | 0 | 0 |
| E083 | Non-file (hermes transcript) | 1.0000 | 0.5000 | -0.5000 | 1 | 0 |
| E085 | Non-file (hermes transcript) | 1.0000 | 0.5000 | -0.5000 | 1 | 0 |
| E006 | ADR (Consequences) | 1.0000 | 0.6309 | -0.3691 | 1 | 0 |
| E019 | ADR (Context) | 1.0000 | 0.6309 | -0.3691 | 1 | 0 |
| E010 | ADR (Context) | 0.6309 | 0.5000 | -0.1309 | 0 | 0 |
| E049 | ADR (Context) | 0.6309 | 0.5000 | -0.1309 | 0 | 0 |
| E003 | ADR (Consequences) | 0.5000 | 0.3869 | -0.1131 | 0 | 0 |

## Drift-confound check

Regression queries whose ground truth touches the two mid-re-chunk drift paths (`pi-badger-integration` `docs/reference/extension-catalog.md`, `docs/howto/update-integrations.md`) are marked drift-confounded, not budget-caused.
Flagged regression queries: **0**.

None: no regression query's anchors or served top-8 lists touch the two paths.
