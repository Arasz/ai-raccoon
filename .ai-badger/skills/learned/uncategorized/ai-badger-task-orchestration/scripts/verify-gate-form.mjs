// hermes-verify — owner-gate review form verifier (see ai-badger-task-orchestration).
// Usage: node verify-gate-form.mjs <form.html> [watch.sh]
// Checks CONFIG binding (storageKey/outName/expectedDir), decision-card integrity, JS syntax,
// no stale template strings, and a DOM-stubbed render smoke run with BOTH script blocks in
// ONE shared vm context (browser semantics: CONFIG/DECISIONS are page globals).
import { readFileSync } from "node:fs";
import vm from "node:vm";

const [formPath, watchPath] = process.argv.slice(2);
const html = readFileSync(formPath, "utf8");
let failures = 0;
const check = (cond, msg) => {
  console.log((cond ? "PASS" : "FAIL") + "  " + msg);
  if (!cond) failures++;
};

// 1. Every inline <script> parses.
const scripts = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].map((m) => m[1]);
scripts.forEach((s, i) => {
  try { new Function(s); check(true, `script[${i}] syntax OK`); }
  catch (e) { check(false, `script[${i}] syntax: ${e.message}`); }
});

// 2. CONFIG + DECISIONS from block 0 (browser-global semantics).
const ctx = vm.createContext({});
vm.runInContext(scripts[0], ctx);
const CONFIG = ctx.CONFIG;
const DECISIONS = ctx.DECISIONS;
check(!!CONFIG && typeof CONFIG.storageKey === "string" && CONFIG.storageKey.length > 0,
  "CONFIG.storageKey set (must be unique per review)");
check(typeof CONFIG.outName === "string" && CONFIG.outName.endsWith(".md"), "CONFIG.outName is a .md filename");
check(typeof CONFIG.expectedDir === "string" && CONFIG.expectedDir.length > 0, "CONFIG.expectedDir set");
check(Array.isArray(DECISIONS) && DECISIONS.length > 0, `DECISIONS array (${DECISIONS.length} cards)`);
const ids = DECISIONS.map((d) => d.id);
check(new Set(ids).size === ids.length, "card ids unique");
for (const d of DECISIONS) {
  check(typeof d.claim === "string" && d.claim.length > 3, `${d.id}: claim`);
  check(Array.isArray(d.detail) && d.detail.length >= 1, `${d.id}: detail`);
  check(typeof d.why === "string" && d.why.length > 3, `${d.id}: why`);
  check(typeof d.placeholder === "string", `${d.id}: placeholder`);
}

// 3. Render smoke run: run BOTH blocks in ONE shared context (as the browser does —
//    CONFIG/DECISIONS are globals). Running block 1 via new Function in isolation throws
//    "CONFIG is not defined" — a HARNESS bug, not a form defect.
const captured = [];
const mkEl = () => ({
  className: "", dataset: {}, textContent: "", innerHTML: "", hidden: false,
  classList: { toggle() {} },
  appendChild(c) { if (c && c.dataset && c.dataset.id) captured.push(c.dataset.id); },
  setAttribute() {}, addEventListener() {},
  querySelector: () => null, querySelectorAll: () => [],
  value: "", checked: false,
});
const els = new Map();
const doc = {
  getElementById: (id) => { if (!els.has(id)) els.set(id, mkEl()); return els.get(id); },
  createElement: () => mkEl(),
  addEventListener() {}, querySelectorAll: () => [],
};
const ctx2 = vm.createContext({
  document: doc,
  localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
  navigator: {}, indexedDB: {},
});
try {
  vm.runInContext(scripts.join("\n;\n"), ctx2, { timeout: 5000 });
  check(JSON.stringify(captured) === JSON.stringify(ids),
    `render appends all ${ids.length} cards in order (got ${captured.length})`);
} catch (e) {
  check(false, "render loop threw: " + e.message);
}

// 4. The live watch targets the same output name (when a watch script is supplied).
if (watchPath) {
  const watch = readFileSync(watchPath, "utf8");
  check(watch.includes(CONFIG.outName), "watch script targets CONFIG.outName");
  check(/seq 1 \d+/.test(watch), "watch loop is capped (finite stopping condition)");
}

console.log(failures === 0 ? "ALL CHECKS PASSED" : failures + " FAILURE(S)");
process.exit(failures === 0 ? 0 : 1);