# Infra Swap Traps — Sync-over-Async & Static I/O

## Sync-over-Async

- Pattern: `search_files pattern: GetAwaiter\(\)\.GetResult\(\)|\.Result file_glob: *.cs path: src/Infrastructure`
- Found: `PeachPdfCvRenderer.cs:33`, `PeachPdfLetterRenderer.cs:22`
- Why risky: comment "Must not be called from thread with SynchronizationContext" — Durable activities today have none, but future HTTP preview would deadlock + AggregateException wrapping.
- Fix: `ICvPdfRenderer.RenderAsync` + sync wrapper, or `ConfigureAwait(false)` + unwrap.

## Static I/O

- Pattern: `search_files pattern: static class file_glob: *.cs` then grep body for `GetManifestResourceStream|File\.`
- Found: `CvHtmlBuilder:19,339`, `LetterHtmlBuilder:17`, `SimpleTemplate:14` (pure — ok)
- Rule: `static-classes.md` allows only extensions/constants/pure. I/O → injectable `ICvHtmlBuilder` singleton, testable.
- Evidence: DI `PeachPdfCvRenderer` singleton calls static directly, hiding seam.

## Page-Count Double Parse

- `PeachPdfCvRenderer.CountPages` re-opens `pdfBytes` via PdfPig. Consider `(bytes,count)` tuple from single generation.
