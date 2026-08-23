#!/usr/bin/env node
import {existsSync, readFileSync, statSync} from "node:fs";
import path from "node:path";

const root = process.cwd();
const modelDir = process.env.AGENT_INSTRUCTIONS_DIR ?? ".ai-badger/agent-instructions";
const modelPath = path.join(root, modelDir, "model.json");
const errors = [];

function readJson(filePath) {
  try {
    return JSON.parse(readFileSync(filePath, "utf8"));
  } catch (error) {
    errors.push(`Failed to parse ${path.relative(root, filePath)}: ${error.message}`);
    return undefined;
  }
}

function readText(relativePath) {
    const absolute = path.join(root, relativePath);
    const {size} = statSync(absolute);
    if (size > MAX_SCAN_BYTES) {
        throw new Error(`${relativePath} is too large to scan (${size} bytes)`);
    }
    return readFileSync(absolute, "utf8");
}

// A model is data, and these two limits keep a bad one from becoming a hang rather than an
// error: an unbounded pattern (catastrophic backtracking) or an unbounded input (review F-39).
const MAX_PATTERN_LENGTH = 500;
const MAX_SCAN_BYTES = 1_000_000;

// True when a quantifier is applied to a group. `^(a+)+$` is seven characters, so the length
// cap above never sees it, and it backtracks catastrophically against a non-matching input
// (review B16). Node has no regex timeout, so such a pattern is refused rather than run.
function quantifiesAGroup(pattern) {
    let inClass = false;
    for (let i = 0; i < pattern.length; i += 1) {
        const ch = pattern[i];
        if (ch === "\\") {
            i += 1;
        } else if (inClass) {
            inClass = ch !== "]";
        } else if (ch === "[") {
            inClass = true;
        } else if (ch === ")" && "*+?{".includes(pattern[i + 1])) {
            return true;
        }
    }
    return false;
}

function compilePattern(pattern, flags) {
    if (typeof pattern !== "string" || pattern.length > MAX_PATTERN_LENGTH) {
        throw new Error(`pattern too long (over ${MAX_PATTERN_LENGTH} chars); simplify it`);
    }
    if (quantifiesAGroup(pattern)) {
        throw new Error(`nested quantifier in pattern ${pattern}: a quantified group backtracks `
            + `catastrophically; rewrite it without a quantifier after ")"`);
    }
    return new RegExp(pattern, flags);
}

function matchesAny(text, patterns) {
    return patterns.some((pattern) => compilePattern(pattern, "is").test(text));
}

function checkRule(rule, kind) {
  for (const relativePath of rule.mustAppearIn) {
    const absolutePath = path.join(root, relativePath);
    if (!existsSync(absolutePath)) {
      errors.push(`${kind} ${rule.id} expects missing file ${relativePath}`);
      continue;
    }

    const text = readText(relativePath);
    if (!matchesAny(text, rule.patterns)) {
      errors.push(`${relativePath} does not mention ${kind} ${rule.id}: ${rule.summary ?? rule.id}`);
    }
  }
}

if (!existsSync(modelPath)) {
  errors.push(`Missing ${path.join(modelDir, "model.json")}`);
} else {
  const model = readJson(modelPath);
  if (model) {
      try {
      for (const invariant of model.sharedPolicy?.nonNegotiableInvariants ?? []) {
          checkRule(invariant, "invariant");
      }

      for (const category of model.sharedPolicy?.reviewCategories ?? []) {
          checkRule(category, "review category");
      }
      } catch (error) {
          // A guard rejection is a reported failure, not a stack trace.
          errors.push(error.message);
    }
  }
}

if (errors.length > 0) {
  console.error("Agent instruction drift detected:");
  for (const error of errors) {
    console.error(`- ${error}`);
  }
  process.exit(1);
}

console.log("Agent instruction drift check passed.");
