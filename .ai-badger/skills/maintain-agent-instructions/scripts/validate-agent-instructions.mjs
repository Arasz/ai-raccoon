#!/usr/bin/env node
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import path from "node:path";

const root = process.cwd();
const modelDir = process.env.AGENT_INSTRUCTIONS_DIR ?? ".ai-badger/agent-instructions";
const modelPath = path.join(root, modelDir, "model.json");
const schemaPath = path.join(root, modelDir, "schema.json");

const errors = [];
const warnings = [];

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
  const { size } = statSync(absolute);
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

function parseHeadingSpec(heading) {
  return typeof heading === "string" ? { text: heading, metadata: undefined } : heading;
}

function hasHeading(text, heading) {
  const spec = parseHeadingSpec(heading);
  const escaped = spec.text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  return new RegExp(`^#{1,6}\\s+${escaped}\\s*$`, "im").test(text);
}

function hasSectionMetadata(text, heading) {
  const spec = parseHeadingSpec(heading);
  if (!spec.metadata || Object.keys(spec.metadata).length === 0) {
    return true;
  }

  const escaped = spec.text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const headingMatch = new RegExp(`^#{1,6}\\s+${escaped}\\s*$`, "im").exec(text);
  if (!headingMatch) {
    return false;
  }

  const afterHeading = text.slice(headingMatch.index + headingMatch[0].length);
  const metadataMatch = afterHeading.match(/^\r?\n\s*<!--\s*agent-section:\s*(\{[^]*?\})\s*-->/);
  if (!metadataMatch) {
    return false;
  }

  try {
    const actual = JSON.parse(metadataMatch[1]);
    return Object.entries(spec.metadata).every(([key, value]) => actual[key] === value);
  } catch {
    return false;
  }
}

function validateInstructionFrontmatter(relativePath, frontmatter) {
  if (!frontmatter) {
    return;
  }

  const text = readText(relativePath);
  const match = text.match(/^---\n([\s\S]*?)\n---\n/);
  if (!match) {
    errors.push(`${relativePath} is missing YAML frontmatter`);
    return;
  }

  const body = match[1];
  if (frontmatter.descriptionRequired && !/^description:\s*/m.test(body)) {
    errors.push(`${relativePath} frontmatter is missing description`);
  }
  if (frontmatter.applyToRequired && !/^applyTo:\s*/m.test(body)) {
    errors.push(`${relativePath} frontmatter is missing applyTo`);
  }
}

if (!existsSync(schemaPath)) {
  errors.push(`Missing ${path.join(modelDir, "schema.json")}`);
}

let guardFailure = "";
const model = existsSync(modelPath) ? readJson(modelPath) : undefined;
if (!model) {
  errors.push(`Missing ${path.join(modelDir, "model.json")}`);
} else {
  try {
  if (model.version !== 1) {
    errors.push(`Unsupported model version: ${model.version}`);
  }

  for (const [key, spec] of Object.entries(model.files ?? {})) {
    const absolutePath = path.join(root, spec.path);
    if (spec.required && !existsSync(absolutePath)) {
      errors.push(`Required file missing (${key}): ${spec.path}`);
      continue;
    }

    if (existsSync(absolutePath) && !statSync(absolutePath).isFile()) {
      errors.push(`Expected file but found non-file (${key}): ${spec.path}`);
      continue;
    }

    if (existsSync(absolutePath) && spec.warnAboveLines) {
      const lineCount = readFileSync(absolutePath, "utf8").split(/\r?\n/).length;
      if (lineCount > spec.warnAboveLines) {
        warnings.push(`${spec.path} has ${lineCount} lines; warning threshold is ${spec.warnAboveLines}`);
      } else if (spec.maxRecommendedLines && lineCount > spec.maxRecommendedLines) {
        warnings.push(`${spec.path} has ${lineCount} lines; recommended maximum is ${spec.maxRecommendedLines}`);
      }
    }
  }

  for (const [key, spec] of Object.entries(model.directories ?? {})) {
    const absolutePath = path.join(root, spec.path);
    if (spec.required && !existsSync(absolutePath)) {
      errors.push(`Required directory missing (${key}): ${spec.path}`);
      continue;
    }

    if (existsSync(absolutePath) && !statSync(absolutePath).isDirectory()) {
      errors.push(`Expected directory but found non-directory (${key}): ${spec.path}`);
      continue;
    }

    if (existsSync(absolutePath) && spec.allowedFiles) {
      const actualFiles = readdirSync(absolutePath).filter((entry) => statSync(path.join(absolutePath, entry)).isFile());
      for (const allowedFile of spec.allowedFiles) {
        if (!actualFiles.includes(allowedFile)) {
          errors.push(`Directory ${spec.path} is missing expected file ${allowedFile}`);
        }
      }
      for (const actualFile of actualFiles) {
        if (!spec.allowedFiles.includes(actualFile)) {
          warnings.push(`Directory ${spec.path} contains unmodeled file ${actualFile}`);
        }
      }
    }
  }

  for (const [relativePath, headings] of Object.entries(model.validation?.requiredHeadings ?? {})) {
    const absolutePath = path.join(root, relativePath);
    if (!existsSync(absolutePath)) {
      errors.push(`Cannot validate headings; missing file ${relativePath}`);
      continue;
    }

    const text = readText(relativePath);
    for (const heading of headings) {
      const spec = parseHeadingSpec(heading);
      if (!hasHeading(text, spec)) {
        errors.push(`${relativePath} missing heading "${spec.text}"`);
        continue;
      }
      if (!hasSectionMetadata(text, spec)) {
        errors.push(`${relativePath} heading "${spec.text}" is missing required agent-section metadata`);
      }
    }
  }

  for (const [relativePath, patterns] of Object.entries(model.validation?.requiredPatterns ?? {})) {
    if (!existsSync(path.join(root, relativePath))) {
      errors.push(`Cannot validate required patterns; missing file ${relativePath}`);
      continue;
    }
    const text = readText(relativePath);
    for (const pattern of patterns) {
      if (!matchesAny(text, [pattern])) {
        errors.push(`${relativePath} missing required pattern /${pattern}/i`);
      }
    }
  }

  for (const [relativePath, patterns] of Object.entries(model.validation?.forbiddenPatterns ?? {})) {
    if (!existsSync(path.join(root, relativePath))) {
      continue;
    }
    const text = readText(relativePath);
    for (const pattern of patterns) {
      if (matchesAny(text, [pattern])) {
        errors.push(`${relativePath} contains forbidden pattern /${pattern}/i`);
      }
    }
  }

  for (const [key, instructionSet] of Object.entries(model.instructionSets ?? {})) {
    const relativePath = instructionSet.path;
    if (!existsSync(path.join(root, relativePath))) {
      errors.push(`Instruction set missing (${key}): ${relativePath}`);
      continue;
    }

    validateInstructionFrontmatter(relativePath, instructionSet.frontmatter);
    const text = readText(relativePath);
    for (const topic of instructionSet.requiredTopics ?? []) {
      if (!matchesAny(text, topic.patterns)) {
        errors.push(`${relativePath} missing required topic ${topic.id}`);
      }
    }
  }
  } catch (error) {
    // A guard rejection is a reported failure, not a stack trace.
    guardFailure = error.message;
  }
}

if (guardFailure) {
  errors.push(guardFailure);
}

if (warnings.length > 0) {
  console.log("Agent instruction validation warnings:");
  for (const warning of warnings) {
    console.log(`- ${warning}`);
  }
  console.log("");
}

if (errors.length > 0) {
  console.error("Agent instruction validation failed:");
  for (const error of errors) {
    console.error(`- ${error}`);
  }
  process.exit(1);
}

console.log("Agent instruction validation passed.");
