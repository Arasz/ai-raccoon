## 13. Docs fold-in checklist

When shipping MCP tools alongside a feature, update these docs:

| Doc                                       | What to add                                            |
|-------------------------------------------|--------------------------------------------------------|
| `docs/functional-specification.md` §2     | New REST endpoint rows in the API table                |
| `docs/functional-specification.md` §3     | New MCP tool names in the tool list; update tool count |
| `docs/features/README.md`                 | Mark feature dossier as `Shipped`                      |
| `docs/features/<feature>/requirements.md` | Update status line to `Shipped`                        |
| `docs/architecture.md` §5                 | Extension-point table rows (if new interfaces added)   |
| `docs/data-model.md`                      | New entities/records (if any)                          |
| `docs/flows.md`                           | New flow diagrams (if any)                             |

If architecture.md, data-model.md, and flows.md already have the content from a prior task, only update functional-specification.md and the status markers.
