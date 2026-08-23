---
name: browser-usage
description: >-
  Use when interacting with web pages, running manual E2E browser tests, testing UI flows, or
  inspecting live web surfaces via Playwright MCP. Employs text-first accessibility snapshots
  and element refs, network inspection, form filling, and custom Playwright script execution.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [browser, playwright, mcp, e2e, ui-testing, accessibility]
    related_skills: [mcp-index, dogfood, artifact-verification]
---

# browser-usage

Universal browser automation and verification skill using Playwright MCP (`@playwright/mcp`). Operates on structured accessibility tree snapshots and element reference tokens (`[ref=eN]`), enabling reliable interaction, inspection, and
end-to-end verification without vision models.

## When NOT to Use

- Plain HTTP REST API calls or JSON fetching — use HTTP tools (`curl`, `web_extract`, `apiFetch`) instead of spinning up a full browser engine.
- Headless unit test runs in CI — use your framework's native test runner (`bun test`, `dotnet test`, `vitest`).
- Static documentation or source code queries — inspect local project files directly.

## Core Architecture: Text-First Accessibility Snapshots

Playwright MCP operates primarily on the browser's accessibility tree rather than raw pixel coordinates or fragile CSS selectors:

1. **Snapshot**: `browser_snapshot` returns a hierarchical text tree of interactive and semantic elements:
   ```text
   - heading "Dashboard" [level=1]
   - textbox "Job Title or Keywords" [ref=e12]
   - button "Search" [ref=e15]
   - list:
     - listitem:
       - link "Senior .NET Engineer" [ref=e22]
       - text: "Remote · Posted 2 days ago"
   ```
2. **Interact by Ref**: Tools accept the `ref` identifier directly (e.g. `ref="e12"` or `element="e12"`), resolving precisely to the underlying DOM node without brittle selector guessing.
3. **Inspect**: Visual evidence (`browser_take_screenshot`), network logs (`browser_network_requests`), and console streams (`browser_console_messages`) supplement the accessibility model.

## Primary Workflows

### 1. Basic Navigation and Page Interaction

```python
# 1. Navigate to target URL
browser_navigate(url="http://localhost:5173/applications")

# 2. Capture the page structure
snapshot = browser_snapshot()

# 3. Target elements using ref IDs from the snapshot
browser_click(element="e15")
browser_type(element="e12", text="Lead Engineer")
browser_press_key(key="Enter")
```

### 2. Form Filling and Option Selection

- **Single Input**: `browser_type(element="e5", text="value")`
- **Batch Form Population**: `browser_fill_form(fields={"e5": "user@example.com", "e8": "password123"})`
- **Dropdowns / Selects**: `browser_select_option(element="e9", values=["openRouter"])`
- **Key Navigation**: `browser_press_key(key="Tab")`, `browser_press_key(key="Escape")`

### 3. Verification and Assertions

- **Wait for State Changes**: `browser_wait_for(text="Application submitted successfully")` or wait for element state transitions.
- **Find Elements**: `browser_find(query="Save changes")` locates matching accessibility nodes.
- **Visual Capture**: `browser_take_screenshot(path="artifacts/dashboard.png")` records full-page or element screenshots for verification evidence.

### 4. Network and Diagnostic Inspection

- **Console Logs**: `browser_console_messages()` inspects runtime errors, warnings, and unhandled exceptions.
- **Network Traffic**: `browser_network_requests()` lists all HTTP calls made since page load.
- **Request Inspection**: `browser_network_request(request_id=12)` fetches headers, status codes, query params, request bodies, and response payloads to verify API contract adherence.

### 5. Multi-Tab, Responsive, and Dialog Management

- **Responsive Testing**: `browser_resize(width=390, height=844)` switches to mobile viewports to test drawer menus and responsive layouts.
- **Tabs**: `browser_tabs(action="list")`, `browser_tabs(action="create", url="...")`,
  `browser_tabs(action="select", tab_id=1)` manages multi-page workflows.
- **Dialogs**: `browser_handle_dialog(action="accept")` or `browser_handle_dialog(action="dismiss")`
  responds to native browser alert, confirm, and prompt dialogs.

### 6. Complex Scripts (`browser_run_code_unsafe`)

When multi-step logic requires tight feedback loops (e.g. measuring dynamic DOM layout, evaluating custom predicates, or running assertions across multiple elements), execute custom Playwright code directly in the server process:

```javascript
async (page) => {
  const rowCount = await page.locator('[data-slot="table-row"]').count();
  const headings = await page.getByRole('heading', { level: 2 }).allTextContents();
  return { rowCount, headings };
}
```

## Configuration & Server Execution

- **Headed vs Headless**: Runs headed by default for live observation. Use `--headless` in CI or headless environments.
- **Browser Engines**: Supports `--browser=chrome`, `--browser=firefox`, `--browser=webkit`, or `--browser=msedge`.
- **User Profiles**:
    - *Persistent* (default): Preserves login cookies and localStorage in per-workspace cache dirs.
    - *Isolated*: `--isolated` starts fresh sessions. State can be restored via `--storage-state`.
    - *Extension*: `--extension` connects directly to existing browser tabs via the Playwright Extension.
- **Standalone Server**: `npx @playwright/mcp@latest --port 8931` serves over HTTP endpoint
  `http://localhost:8931/mcp`.

## Gotchas

- **Stale Element Refs**: Any page navigation, form submission, dialog open/close, or DOM mutation invalidates existing `ref=eN` tokens. Always re-capture `browser_snapshot` after triggering UI state changes.
- **Headed Mode on Headless Environments**: Running in headed mode on Linux servers without a virtual display (Xvfb) or display server fails on launch. Pass `--headless` in containerized or CI environments.
- **`browser_run_code_unsafe` Security**: This tool executes arbitrary JavaScript in the Playwright server process (RCE-equivalent). Only execute trusted scripts and avoid interpolating unescaped external inputs into code strings.
- **Network Idle Stalls**: Avoid waiting on blanket `networkidle` states when background SSE/WebSocket streams or analytics polling are active; use `browser_wait_for` targeting concrete UI elements or text instead.

## Verification Checklist

- [ ] `browser_navigate` loads target application URL successfully
- [ ] `browser_snapshot` captures accessible elements with valid `ref` tokens
- [ ] User interactions (`browser_click`, `browser_type`, `browser_fill_form`) execute without errors
- [ ] Diagnostic streams (`browser_console_messages`, `browser_network_requests`) checked for errors
- [ ] `browser_close` cleanly terminates browser session upon completion
