# C# String Interpolation Gotchas

## `\b` is backspace, not regex word boundary

In C# interpolated strings (`$"..."`), escape sequences are processed by the C# compiler, NOT by the regex engine.

```csharp
// WRONG — \b is the backspace character (U+0008) at compile time
var pattern = $"\b{Regex.Escape(keyword)}\b";
// Regex engine sees: \x08VP\x08 — matches literal backspace, NOT word boundary

// CORRECT — \\b produces literal \b at runtime, which regex interprets as word boundary
var pattern = $"\\b{Regex.Escape(keyword)}\\b";

// ALSO CORRECT — verbatim interpolated string (no escape processing)
var pattern = $@"\b{Regex.Escape(keyword)}\b";
```

**Detection**: The bug is invisible — the code compiles, the regex runs, but never matches.
The only symptom is `Regex.IsMatch` returning false for patterns that should match.

**Verification**: Check compiled IL or raw bytes. In the source file, `\\b` = `5c 62` (correct),
while `$"\b"` in C# source = `5c 62` (file bytes) but compiler transforms to `08` (backspace).

**Debugging shortcut**: Add `Console.WriteLine(BitConverter.ToString(Encoding.UTF8.GetBytes(pattern)))`
to see the actual runtime bytes. `5C-62` = word boundary, `08` = backspace.

## Other escape sequences with the same trap

| C# `$"..."` escape | Character | Regex meaning | Fix |
|---|---|---|---|
| `\b` | Backspace (0x08) | Word boundary | `\\b` or `$@"\b"` |
| `\d` | **Compile error** | Digit | Not an issue — C# rejects unknown escapes |
| `\n` | Newline | Literal newline | `\\n` for regex `\n` |
| `\t` | Tab | Literal tab | `\\t` for regex `\t` |

## When to suspect this bug

- Regex `IsMatch` returns false for patterns that work in Python/JS/regex101
- Word boundary `\b` never matches, even at obvious word starts
- The pattern compiles and runs without exceptions — just silently fails

## Raw string literals: content on the SAME line as `"""` = single-line literal (CS8997)

A raw string literal whose opening `"""` is followed by non-whitespace on the same line is a
SINGLE-LINE literal — it cannot contain newlines. Any embedded newline then fails the build
with `CS8997: Unterminated raw string literal` plus a cascade of bogus syntax errors
(`CS1519 Invalid token 'if'`, etc.) pointing at the script content.

```csharp
// WRONG — CS8997: multi-line content after same-line opener
const string Script = """#!/bin/sh
echo hi
""";

// CORRECT — content starts on the line AFTER the opening delimiter
// (the leading newline is excluded from the value by the spec)
const string Script = """
#!/bin/sh
echo hi
""";
```

**Detection**: the error points at the OPENING line (line/col of the `"""`), not the actual
offender, and the follow-on errors make it look like the whole block is garbage. If a
multi-line raw string fails to compile, check whether any content shares the opener's line.
Single-line raw strings (no newlines) may keep content on the opener's line safely.
