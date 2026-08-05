#!/usr/bin/env bash
# Verifies the packed global tool ships the bundled ONNX model + vocab, byte-verified
# against the pinned SHA-256 (FR-NM-3; see docs/work/features-native-memory/native-memory.feature).
# Run locally after touching the model or the pack layout, before dispatching publish.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VER="$(sed -n 's:.*<PackageVersion>\([^<]*\)</PackageVersion>.*:\1:p' "$REPO_ROOT/src/AiRaccoon/AiRaccoon.csproj")"
RID="$(dotnet --info 2>/dev/null | awk -F': ' '/RID:/{gsub(/^[ \t]+|[ \t]+$/, "", $2); print $2; exit}')"
if [ -z "$RID" ]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64) RID=osx-arm64 ;;
    Darwin-x86_64) RID=osx-x64 ;;
    Linux-x86_64) RID=linux-x64 ;;
    Linux-aarch64) RID=linux-arm64 ;;
    *) echo "cannot determine host RID (dotnet --info unavailable)" >&2; exit 1 ;;
  esac
fi

TMP_BASE="${TMPDIR:-/tmp}"
TMP_BASE="${TMP_BASE%/}"
TMP="$(mktemp -d "$TMP_BASE/ai-raccoon-verify-pkg.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

dotnet pack "$REPO_ROOT/src/AiRaccoon/AiRaccoon.csproj" -c Release -p:RuntimeIdentifiers="$RID" -o "$TMP" --nologo
NUPKG="$TMP/arasz.ai-raccoon.$VER.nupkg"
[ -f "$NUPKG" ] || { echo "FAIL: $NUPKG was not produced" >&2; exit 1; }

# unzip -Z1 lists exact stored paths (unzip -l wraps long filenames, breaking grep).
# Grep against a FILE, never a pipe: grep -q exits on the first match and SIGPIPEs
# the still-writing unzip, which pipefail turns into a false failure (observed race).
unzip -Z1 "$NUPKG" > "$TMP/entries.txt"
for entry in "Models/model_qint8_arm64.onnx" "Models/vocab.txt"; do
  if ! grep -qx "$entry" "$TMP/entries.txt"; then
    echo "FAIL: $entry missing from $NUPKG" >&2
    exit 1
  fi
  echo "present in package: $entry"
done

unzip -p "$NUPKG" "Models/model_qint8_arm64.onnx" > "$TMP/model.onnx"
ACTUAL="$(shasum -a 256 "$TMP/model.onnx" | cut -d' ' -f1)"
EXPECTED="4278337fd0ff3c68bfb6291042cad8ab363e1d9fbc43dcb499fe91c871902474"
if [ "$ACTUAL" != "$EXPECTED" ]; then
  echo "FAIL: model sha256 mismatch: expected $EXPECTED, got $ACTUAL" >&2
  exit 1
fi
echo "model sha256 verified: $ACTUAL"
echo "OK: arasz.ai-raccoon.$VER.nupkg ships the verified bundled model"
