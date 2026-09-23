# Refinement feedback — ADR-0106 P5 findings — one release blocker, seven residuals

<!-- refinement-form: refinement:2026-09-23-identity-proof-p5-findings:v1 · saved 2026-09-23T11:32:51.382Z · answered 10/10 -->

Source document: `docs/work/implementation/p5-e2e-report.md`

All ten decisions APPROVED by the owner: F1, F7, F2, F3 (fix in PR #657); F4 (#658), F6 (#659),
F5 (ADR-0106 residual + #660); F8 accepted out of D4 scope; H1 (README tag at release bump);
H2 (#661). F4 carried the note "MCP client? Explain" — answered in session: the MCP client is the
agent host on the proxy's stdio; a 2025-11-25-revision client reopens its session, which costs a
second fallback backend on a squatted port.

R1 (added after the form was saved) was not answered within the prompt window; the recommended
option (Retry=Never carve-out, 06120aa6) was applied pending owner confirmation.

<!-- end refinement feedback -->
